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
& dotnet build "$PSScriptRoot/PiuLink.Windows.wixproj" -c Release "-p:SuiteVersion=$version" "-p:HostMsi=$HostMsi" "-p:ClientMsi=$ClientMsi" -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Windows suite build failed.' }
$file = "PiuLink-Windows-$version-win-x64.exe"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "bin/x64/Release/$file") -Destination (Join-Path $out $file)
