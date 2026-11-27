[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Bundle,
    [Parameter(Mandatory)][string]$HostMsi,
    [Parameter(Mandatory)][string]$ClientMsi,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$Bundle = (Resolve-Path -LiteralPath $Bundle).Path
$inputs = @{ Host=(Resolve-Path -LiteralPath $HostMsi).Path; Client=(Resolve-Path -LiteralPath $ClientMsi).Path }
# A fresh directory prevents an earlier extraction from masking missing media.
$work = (New-Item -ItemType Directory -Path $WorkDirectory).FullName
$targets = & dotnet msbuild "$PSScriptRoot/PiuLink.Windows.wixproj" -nologo -getProperty:WixTargetsPath
if ($LASTEXITCODE) { throw 'Cannot locate the project WiX SDK.' }
$wix = Join-Path (Split-Path $targets) 'net472\x64\wix.exe'
$null = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $targets) 'net472\WixToolset.Dtf.WindowsInstaller.dll'))
$payload = Join-Path $work 'payload'
$ba = Join-Path $work 'ba'
& $wix burn extract $Bundle -o $payload -oba $ba
if ($LASTEXITCODE) { throw 'Cannot extract the suite.' }
[xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $ba 'manifest.xml')
$chain = @($manifest.SelectNodes("//*[local-name()='Chain']/*[local-name()='MsiPackage']"))
if (($chain.Id -join ',') -cne 'Host,Client') { throw 'Unexpected installation chain.' }
$attached = $manifest.SelectNodes("/*/*[local-name()='Payload']")
if (!$attached.Count) { throw 'Missing attached installation payloads.' }
foreach ($item in $attached) {
    if ($item.Packaging -ne 'embedded' -or !$item.Container -or $item.DownloadUrl) { throw "External payload: $($item.FilePath)" }
    $path = Join-Path (Join-Path $payload $item.Container) $item.FilePath
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing offline payload: $($item.FilePath)"
    }
}

function Get-AdminFiles($msi, $name) {
    $dest = Join-Path $work $name
    $log = Join-Path $work "$name.log"
    $p = Start-Process msiexec.exe -WindowStyle Hidden -Wait -PassThru -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$dest`" /L*v `"$log`""
    if ($p.ExitCode) { throw "Windows Installer returned $($p.ExitCode) while extracting $name. See $log." }
    $files = @{}
    foreach ($file in (Get-ChildItem $dest -File -Recurse)) {
        if ($file.DirectoryName -eq $dest -and $file.Extension -eq '.msi') { continue }
        $files[$file.FullName.Substring($dest.Length + 1)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $files
}

function Get-InstallerTables($msi, $name, $compact = $false) {
    $dir = (New-Item -ItemType Directory -Path (Join-Path $work $name)).FullName
    $db = [WixToolset.Dtf.WindowsInstaller.Database]::new($msi, [WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly)
    try {
        foreach ($table in $db.ExecuteStringQuery('SELECT `Name` FROM `_Tables`')) {
            if ($table -eq 'Media') { continue }
            $idt = Join-Path $dir "$table.idt"
            $db.Export($table, $idt)
            if ($table -eq 'File') {
                # Sequence is the last File column; every other field must match.
                $rows = [IO.File]::ReadAllLines($idt)
                if (!$rows[0].EndsWith("`tSequence")) { throw 'Unexpected File schema.' }
                for ($i = 3; $i -lt $rows.Length; $i++) { $rows[$i] = $rows[$i].Substring(0, $rows[$i].LastIndexOf("`t")) }
                [IO.File]::WriteAllLines($idt, $rows)
            } elseif ($table -eq 'Property') {
                $rows = [IO.File]::ReadAllLines($idt) -replace '^ProductCode\t\{[a-fA-F0-9-]{36}\}$', 'ProductCode'
                [IO.File]::WriteAllLines($idt, $rows)
            } elseif ($table -eq 'Upgrade') {
                $rows = [IO.File]::ReadAllLines($idt)
                for ($i = 3; $i -lt $rows.Length; $i++) {
                    $cols = $rows[$i].Split("`t")
                    if ($compact -and $cols[-1] -eq 'WIX_UPGRADE_DETECTED') {
                        if (!([int]$cols[4] -band 512)) { throw 'Compact MSI must also upgrade the equal-version standalone product.' }
                        $cols[4] = [string]([int]$cols[4] -band (-bnot 512))
                        $rows[$i] = $cols -join "`t"
                    }
                }
                [IO.File]::WriteAllLines($idt, $rows)
            }
        }
        $db.Export('_SummaryInformation', (Join-Path $dir 'summary.idt'))
        $summary = Join-Path $dir 'summary.idt'
        $rows = [IO.File]::ReadAllLines($summary) | Where-Object { $_ -notmatch '^9\t' }
        [IO.File]::WriteAllLines($summary, [string[]]$rows)
    } finally { $db.Dispose() }
    $hashes = @{}
    foreach ($file in (Get-ChildItem $dir -Recurse -File)) {
        $hashes[$file.FullName.Substring($dir.Length + 1)] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    return $hashes
}

function Assert-EqualFiles($a, $b, $description) {
    if (!$a.Count -or $a.Count -ne $b.Count) { throw "$description count differs." }
    foreach ($path in $a.Keys) {
        if ($b[$path] -cne $a[$path]) { throw "$description differs: $path" }
    }
}
foreach ($pkg in $chain) {
    $role = $pkg.Id
    $item = $attached | Where-Object Id -eq $role
    $msi = Join-Path (Join-Path $payload $item.Container) $item.FilePath
    if ($role -eq 'Host') {
        & "$PSScriptRoot/../stream-engine/Test-IntegratedInstaller.ps1" -InstallerPath $msi -PublicPreview -RequireCli
    }
    $compact = $pkg.CacheId -like 'PiuLink.*'
    $originalDb = [WixToolset.Dtf.WindowsInstaller.Database]::new($inputs[$role], [WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly)
    try {
        $originalCode = $originalDb.ExecuteStringQuery("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductCode'")[0]
        if (($originalCode -eq $pkg.ProductCode) -eq $compact) { throw "$role product identity does not match the media layout." }
    } finally { $originalDb.Dispose() }
    Assert-EqualFiles (Get-InstallerTables $inputs[$role] "tables-original-$role") (Get-InstallerTables $msi "tables-compact-$role" $compact) "$role installation database"
    $original = Get-AdminFiles $inputs[$role] "reference-$role"
    $restored = Get-AdminFiles $msi "bundle-$role"
    Assert-EqualFiles $original $restored "$role installation files"
    Write-Output "PASS $role - $($original.Count) installed paths and SHA-256 hashes match."
}
