[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EngineDirectory,
    [string]$ShareSigningCertificateThumbprint,
    [string]$PrebuiltShareDirectory,
    [string]$OutputDirectory,
    [switch]$Development,
    [switch]$PublicPreview,
    [string]$MicrophonePackageDirectory,
    [switch]$MicrophoneLayoutTest,
    [switch]$UseThisComputersMicrophone
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$enginePath = [IO.Path]::GetFullPath($EngineDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $enginePath 'piulink-engine.json') -Raw | ConvertFrom-Json
$pin = Get-Content -LiteralPath "$PSScriptRoot/upstream.json" -Raw | ConvertFrom-Json
if ($manifest.engineVersion -ne $pin.engineVersion) { throw "Expected the revised pairing engine $($pin.engineVersion)." }
if (!$manifest.releaseReady -and !$Development) {
    throw 'This engine has not passed release validation. Use -Development only for a development installer.'
}
$prebuiltSharePath = if ($PrebuiltShareDirectory) { [IO.Path]::GetFullPath($PrebuiltShareDirectory) } else { $null }
$shareCertificate = $null
if ($prebuiltSharePath) {
    foreach ($required in @('PiuLink.Share.msix', 'ShareTarget')) {
        if (!(Test-Path (Join-Path $prebuiltSharePath $required))) { throw "Prebuilt share artifact is missing $required." }
    }
    $signature = Get-AuthenticodeSignature (Join-Path $prebuiltSharePath 'PiuLink.Share.msix')
    if ($signature.Status -ne 'Valid') { throw "Prebuilt share MSIX signature is not valid: $($signature.Status)" }
}
else {
    if ([string]::IsNullOrWhiteSpace($ShareSigningCertificateThumbprint)) {
        throw 'Specify a share signing certificate or a signed prebuilt share artifact.'
    }
    $shareThumbprint = $ShareSigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    $shareCertificate = Get-Item -Path "Cert:\CurrentUser\My\$shareThumbprint" -ErrorAction SilentlyContinue
    if (!$shareCertificate) { $shareCertificate = Get-Item -Path "Cert:\LocalMachine\My\$shareThumbprint" -ErrorAction SilentlyContinue }
    if (!$shareCertificate -or !$shareCertificate.HasPrivateKey) {
        throw "Share signing certificate with a private key was not found: $ShareSigningCertificateThumbprint"
    }
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts/piulink-integrated-development' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$project = Join-Path $repoRoot 'apps/windows-host/installers/PiuLink.Host.Setup/PiuLink.Host.Setup.wixproj'
$dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
$hostProject = Join-Path $repoRoot 'apps/windows-host/src/PiuLink.Host/PiuLink.Host.csproj'
$hostProjectXml = [xml][IO.File]::ReadAllText($hostProject)
$hostVersion = $hostProjectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$engineVersion = ([string]$manifest.engineVersion) -replace '[^A-Za-z0-9._-]', '-'
$suffix = if ($Development) { 'Development-unsigned' } else { 'unsigned' }
if ($MicrophoneLayoutTest) {
    if (!$Development) { throw 'Driver layout validation requires a development build.' }
    $suffix = 'LayoutTest-NotInstallable'
}
$packageName = "PiuLink-Host-$hostVersion-Engine-$engineVersion-x64-$suffix"
$internalOutputName = 'PiuLink-Integrated-Development'
$shareBuildArguments = if ($prebuiltSharePath) {
    @("-p:PiuLinkPrebuiltShareDirectory=$prebuiltSharePath")
} else {
    @("-p:PiuLinkShareSigningCertificateThumbprint=$ShareSigningCertificateThumbprint", "-p:PiuLinkSharePublisher=$($shareCertificate.Subject)")
}
$audioArguments = @()
if ($PublicPreview) {
    if (!$Development -or $MicrophonePackageDirectory -or $MicrophoneLayoutTest -or $UseThisComputersMicrophone) {
        throw 'The public development preview packages no microphone driver. Choose a single microphone mode.'
    }
    $audioArguments += '-p:PiuLinkPublicPreview=1'
    $packageName = "PiuLink-Host-$hostVersion-win-x64-preview"
}
if ($UseThisComputersMicrophone) {
    if (!$Development -or $MicrophoneLayoutTest -or $MicrophonePackageDirectory) { throw 'Reusing the local microphone is only supported for a separate local development upgrade.' }
    $mic = Get-CimInstance Win32_PnPSignedDriver -Filter "DeviceClass='MEDIA'" | Where-Object DeviceID -eq 'ROOT\PIULINKVIRTUALMICROPHONE\0000'
    if (!$mic.IsSigned -or $mic.Signer -ne 'Microsoft Windows Hardware Compatibility Publisher' -or $mic.DriverProviderName -ne 'Valve Corporation') {
        throw 'The PiuLink microphone instance must already use the signed legacy driver on this computer.'
    }
    $audioArguments += "-p:LocalMicComputer=$([Environment]::MachineName)"
    $packageName = "PiuLink-Host-$hostVersion-Engine-$engineVersion-x64-LocalExistingMic"
}
if ($MicrophonePackageDirectory) { $audioArguments += "-p:PiuLinkMicrophonePackage=$((Resolve-Path -LiteralPath $MicrophonePackageDirectory).Path)" }
if ($MicrophoneLayoutTest) { $audioArguments += '-p:MicrophoneLayoutTest=1' }
& $dotnet build $project -c Release "-p:OutputName=$internalOutputName" '-p:PiuLinkSelfContained=true' "-p:PiuLinkEngineDirectory=$enginePath" $shareBuildArguments $audioArguments '-p:NuGetAudit=false'
if ($LASTEXITCODE) { throw 'Integrated Host build failed.' }
$msi = Join-Path (Split-Path $project) "bin/x64/Release/$internalOutputName.msi"
& "$PSScriptRoot/Test-IntegratedInstaller.ps1" -InstallerPath $msi -LayoutOnly:$MicrophoneLayoutTest -UseThisComputersMicrophone:$UseThisComputersMicrophone -PublicPreview:$PublicPreview -RequireCli
$destination = Join-Path $outputPath "$packageName.msi"
Copy-Item -LiteralPath $msi -Destination $destination -Force
Write-Output "Integrated installer built at $outputPath. No service was installed or replaced."
