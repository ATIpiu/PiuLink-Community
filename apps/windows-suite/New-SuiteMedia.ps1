[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HostMsi,
    [Parameter(Mandatory)][string]$ClientMsi,
    [Parameter(Mandatory)][string]$Stage,
    [Parameter(Mandatory)][string]$WixTools
)
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $WixTools 'WixToolset.Dtf.WindowsInstaller.dll'))
$native = Join-Path $WixTools 'x64'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $native 'WixToolset.Data.dll'))
$null = [Reflection.Assembly]::LoadFrom((Join-Path $native 'WixToolset.Core.Native.dll'))

function Read-Package($role, $path) {
    $out = (New-Item -ItemType Directory -Path (Join-Path $Stage $role)).FullName
    $files = (New-Item -ItemType Directory -Path (Join-Path $out 'files')).FullName
    $db = [WixToolset.Dtf.WindowsInstaller.Database]::new($path, [WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly)
    $entries = @{}
    try {
        $cabinets = $db.ExecuteStringQuery('SELECT `Cabinet` FROM `Media`')
        $streams = $db.ExecuteStringQuery('SELECT `Name` FROM `_Streams`')
        $otherStreams = @($streams | Where-Object {
            $_ -cne ([char]5 + 'SummaryInformation') -and $_ -cnotmatch '^(Binary|Icon)\.' -and "#$_" -cnotin $cabinets
        })
        if ($otherStreams.Count -or $db.ExecuteStringQuery('SELECT `Name` FROM `_Storages`').Count -or
            !($db.SummaryInfo.WordCount -band 2) -or @($cabinets | Where-Object { !$_ -or !$_.StartsWith('#') }).Count -or
            @($db.ExecuteIntegerQuery('SELECT `Attributes` FROM `File`') | Where-Object { $_ -band 8192 }).Count) {
            Write-Host "Keeping the original $role MSI for its source layout or additional streams."
            return
        }
        $db.Export('_SummaryInformation', (Join-Path $out 'summary.idt'))
        foreach ($cab in $cabinets) {
            $view = $db.OpenView('SELECT `Data` FROM `_Streams` WHERE `Name` = ?')
            try {
                $view.Execute([WixToolset.Dtf.WindowsInstaller.Record]::new([object[]]@($cab.Substring(1))))
                $record = $view.Fetch()
                $cabPath = Join-Path $out 'input.cab'
                try { $record.GetStream(1, $cabPath) } finally { $record.Dispose() }
                $null = [WixToolset.Core.Native.Cabinet]::new($cabPath).Extract($files)
            } finally { $view.Dispose() }
        }
        foreach ($id in $db.ExecuteStringQuery('SELECT `File` FROM `File`')) {
            $source = Join-Path $files $id
            $entries[$id] = [pscustomobject]@{ Id=$id; Path=$source; Hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
        }
    } finally { $db.Dispose() }
    [pscustomobject]@{ Role=$role; Input=$path; Output=(Join-Path $out ([IO.Path]::GetFileName($path))); Entries=$entries }
}

function Write-Cabinet($name, $entries) {
    # Group equal content together. Use the actual cabinet order for the MSI File sequences.
    $inputs = [Collections.Generic.List[WixToolset.Core.Native.CabinetCompressFile]]::new()
    foreach ($entry in ($entries | Sort-Object Hash, Id)) {
        $inputs.Add([WixToolset.Core.Native.CabinetCompressFile]::new($entry.Path, $entry.Id))
    }
    $path = Join-Path $Stage $name
    $cabinet = [WixToolset.Core.Native.Cabinet]::new($path)
    $null = $cabinet.Compress($inputs, [WixToolset.Data.CompressionLevel]::High, 0, 0)
    [pscustomobject]@{ Name=$name; Path=$path; Files=@($cabinet.Enumerate() | ForEach-Object FileId) }
}

function Write-Package($package, $media) {
    # Windows Installer save-as copies table-backed streams (e.g. custom actions),
    # omits standalone streams (the original cabinets), and needs the summary reimported.
    $db = [WixToolset.Dtf.WindowsInstaller.Database]::new($package.Input, $package.Output)
    try {
        $db.Import((Join-Path (Split-Path $package.Output) 'summary.idt'))
        # A change from embedded cabinets to external media is a major MSI upgrade.
        # Reusing the old product code leaves the standalone database cached (repair error 1638).
        $product = [guid]::NewGuid().ToString('B').ToUpperInvariant()
        $db.Execute("UPDATE ``Property`` SET ``Value`` = '$product' WHERE ``Property`` = 'ProductCode'")
        $rules = $db.OpenView("SELECT * FROM ``Upgrade`` WHERE ``ActionProperty`` = 'WIX_UPGRADE_DETECTED'")
        try {
            $rules.Execute()
            $rule = $rules.Fetch()
            if (!$rule) { throw "Missing upgrade rule for $($package.Role)." }
            $rules.Delete($rule)
            $rule['Attributes'] = [int]$rule['Attributes'] -bor 512
            $rules.Insert($rule)
        } finally { if ($rule) { $rule.Dispose() }; $rules.Dispose() }
        $db.Execute('DELETE FROM `Media`')
        $update = $db.OpenView('UPDATE `File` SET `Sequence` = ? WHERE `File` = ?')
        $row = [WixToolset.Dtf.WindowsInstaller.Record]::new(2)
        $sequence = 0
        $disk = 0
        $assigned = 0
        foreach ($cab in $media) {
            $disk++
            foreach ($id in $cab.Files) {
                $sequence++
                if ($package.Entries.ContainsKey($id)) {
                    $row.SetInteger(1, $sequence)
                    $row.SetString(2, $id)
                    $update.Execute($row)
                    $assigned++
                }
            }
            $mediaRow = [WixToolset.Dtf.WindowsInstaller.Record]::new(3)
            try {
                $mediaRow.SetInteger(1, $disk)
                $mediaRow.SetInteger(2, $sequence)
                $mediaRow.SetString(3, $cab.Name)
                $db.Execute('INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (?, ?, ?)', $mediaRow)
            } finally { $mediaRow.Dispose() }
            Copy-Item -LiteralPath $cab.Path -Destination (Split-Path $package.Output)
        }
        if ($assigned -ne $package.Entries.Count) { throw "Incomplete media for $($package.Role)." }
        $db.Commit()
    } finally {
        if ($update) { $update.Dispose() }
        if ($row) { $row.Dispose() }
        $db.Dispose()
    }
    $code = [guid]::NewGuid().ToString('B').ToUpperInvariant()
    $summary = [WixToolset.Dtf.WindowsInstaller.SummaryInfo]::new($package.Output, $true)
    try { $summary.RevisionNumber = $code; $summary.Persist() } finally { $summary.Dispose() }
    [pscustomobject]@{ Msi=$package.Output; CacheId="PiuLink.$($package.Role).$code" }
}

$server = Read-Package 'Host' $HostMsi
if (!$server) { return }
$client = Read-Package 'Client' $ClientMsi
if (!$client) { return }
$serverHashes = [Collections.Generic.HashSet[string]]::new([string[]]$server.Entries.Values.Hash)
$serverHashes.IntersectWith([string[]]$client.Entries.Values.Hash)
$common = @{}
$private = @{ Host=[Collections.Generic.List[object]]::new(); Client=[Collections.Generic.List[object]]::new() }
foreach ($package in $server, $client) {
    foreach ($entry in $package.Entries.Values) {
        $id = $entry.Id
        # A cabinet has one namespace. Keep conflicting keys on their respective role disks.
        $conflict = $server.Entries.ContainsKey($id) -and $client.Entries.ContainsKey($id) -and $server.Entries[$id].Hash -ne $client.Entries[$id].Hash
        if (!$conflict -and $serverHashes.Contains($entry.Hash)) {
            $common[$id] = $entry
        } else {
            $private[$package.Role].Add($entry)
        }
    }
}
$sharedCab = if ($common.Count) { Write-Cabinet 'PiuLinkCommon.cab' $common.Values }
$result = @{}
foreach ($package in $server, $client) {
    $media = @(
        if ($sharedCab) { $sharedCab }
        if ($private[$package.Role].Count) { Write-Cabinet "PiuLink$($package.Role).cab" $private[$package.Role] }
    )
    $result[$package.Role] = Write-Package $package $media
}
$result
