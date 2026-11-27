[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HostMsi,
    [Parameter(Mandatory)][string]$ClientMsi,
    [version]$SuiteVersion,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$HostMsi = (Resolve-Path -LiteralPath $HostMsi).Path
$ClientMsi = (Resolve-Path -LiteralPath $ClientMsi).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
function Read-Product($file, $expectedName) {
    $db = $installer.OpenDatabase($file, 0)
    $view = $db.OpenView("SELECT ``Property``, ``Value`` FROM ``Property``")
    $null = $view.Execute()
    $properties = @{}
    while ($row = $view.Fetch()) { $properties[$row.StringData(1)] = $row.StringData(2) }
    $null = $view.Close()
    if ($properties['ProductName'] -ne $expectedName -or $properties['Manufacturer'] -ne 'ATIPiu Labs') {
        throw "Expected the $expectedName installer: $file"
    }
    return [version]$properties['ProductVersion']
}
$hostVersion = Read-Product $HostMsi 'PiuLink Host'
$version = if ($SuiteVersion) { $SuiteVersion } else { $hostVersion }
$null = Read-Product $ClientMsi 'PiuLink Client'
& "$PSScriptRoot/../stream-engine/Test-IntegratedInstaller.ps1" -InstallerPath $HostMsi -PublicPreview -RequireCli
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "../../artifacts/PiuLink-Windows-$version" }
$out = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$project = Join-Path $PSScriptRoot 'PiuLink.Windows.wixproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = Join-Path $tempRoot ('PiuLink-Suite-' + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $stage
try {
    $cache = @()
    $signed = @($HostMsi, $ClientMsi | Get-AuthenticodeSignature | Where-Object Status -ne 'NotSigned')
    if (!$signed.Count) {
        $targets = & dotnet msbuild $project -nologo -getProperty:WixTargetsPath
        if ($LASTEXITCODE -or !(Test-Path -LiteralPath $targets)) { throw 'Cannot locate the project WiX SDK.' }
        $media = & "$PSScriptRoot/New-SuiteMedia.ps1" -HostMsi $HostMsi -ClientMsi $ClientMsi -Stage $stage -WixTools (Join-Path (Split-Path $targets) 'net472')
        if ($media) {
            $HostMsi = $media.Host.Msi
            $ClientMsi = $media.Client.Msi
            $cache = @("-p:HostCacheId=$($media.Host.CacheId)", "-p:ClientCacheId=$($media.Client.CacheId)")
        }
    } else {
        Write-Host 'Preserving the original signed MSI packages in the bundle.'
    }
    $file = "PiuLink-Windows-$version-win-x64.exe"
    $buildOut = Join-Path $stage 'bundle'
    & dotnet build $project -c Release -o $buildOut "-p:IntermediateOutputPath=$stage/obj/" "-p:SuiteVersion=$version" "-p:HostMsi=$HostMsi" "-p:ClientMsi=$ClientMsi" @cache -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'Windows suite build failed.' }
    Copy-Item -LiteralPath (Join-Path $buildOut $file) -Destination (Join-Path $out $file)
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    if ((Split-Path $resolved -Parent) -eq $tempRoot.TrimEnd('\') -and (Split-Path $resolved -Leaf) -match '^PiuLink-Suite-[0-9a-f-]{36}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Continue
    }
}
