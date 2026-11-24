[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$PiuLinkEngineDirectory,
    [Parameter(Mandatory)]
    [string]$ShareSigningCertificateThumbprint,
    [switch]$Standalone
)

$ErrorActionPreference = 'Stop'
$hostRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $hostRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\windows-host-open-source'
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$resolvedRepo = $repoRoot.TrimEnd('\') + '\'
if (-not $resolvedOutput.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must remain inside the PiuLink repository: $resolvedOutput"
}
$shareThumbprint = $ShareSigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
$shareCertificate = Get-Item -Path "Cert:\CurrentUser\My\$shareThumbprint" -ErrorAction SilentlyContinue
if (!$shareCertificate) {
    $shareCertificate = Get-Item -Path "Cert:\LocalMachine\My\$shareThumbprint" -ErrorAction SilentlyContinue
}
if (!$shareCertificate -or !$shareCertificate.HasPrivateKey) {
    throw "Share signing certificate with a private key was not found: $ShareSigningCertificateThumbprint"
}
$selfContained = if ($Standalone) { 'true' } else { 'false' }
$engineArgument = @()
if (-not [string]::IsNullOrWhiteSpace($PiuLinkEngineDirectory)) {
    $resolvedEngine = [IO.Path]::GetFullPath($PiuLinkEngineDirectory)
    if (-not $resolvedEngine.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Embedded engine directory must remain inside the PiuLink repository: $resolvedEngine"
    }
    $engineArgument = @("-p:PiuLinkEngineDirectory=$resolvedEngine")
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$installerProject = Join-Path $PSScriptRoot 'PiuLink.Host.Setup\PiuLink.Host.Setup.wixproj'
$projectXml = [xml][IO.File]::ReadAllText((Join-Path $hostRoot 'src\PiuLink.Host\PiuLink.Host.csproj'))
$hostVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$outputName = "PiuLink-Host-OpenSource-$hostVersion-unsigned"
& $dotnet build $installerProject `
    --configuration Release `
    -p:OutputName=$outputName `
    -p:PiuLinkSelfContained=$selfContained `
    -p:PiuLinkShareSigningCertificateThumbprint=$ShareSigningCertificateThumbprint `
    "-p:PiuLinkSharePublisher=$($shareCertificate.Subject)" `
    @engineArgument `
    -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) {
    throw "PiuLink Host MSI build failed with exit code $LASTEXITCODE."
}

$builtMsi = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'PiuLink.Host.Setup\bin') `
    -Filter "$outputName.msi" -Recurse -File |
    Select-Object -First 1
if ($null -eq $builtMsi) {
    throw 'The Host MSI was not found after a successful build.'
}

$destination = Join-Path $resolvedOutput $builtMsi.Name
Copy-Item -LiteralPath $builtMsi.FullName -Destination $destination -Force
Write-Output "Open-source Host MSI created at $destination"
if (-not $Standalone) {
    Write-Output 'Compact build: Microsoft .NET 8 Desktop Runtime x64 is required on the target PC.'
}
