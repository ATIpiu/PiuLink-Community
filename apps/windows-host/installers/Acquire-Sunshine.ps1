[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'manifests\sunshine-windows-x64.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'cache')
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported Sunshine manifest schema: $($manifest.schemaVersion)"
}

$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$targetPath = Join-Path $OutputDirectory $manifest.fileName
$licensePath = Join-Path $PSScriptRoot "licenses\$($manifest.licenseFileName)"

if (-not (Test-Path -LiteralPath $licensePath)) {
    throw "The reviewed Sunshine license file is missing: $licensePath"
}

$actualLicenseHash = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash
if ($actualLicenseHash -ne $manifest.licenseSha256) {
    throw "Sunshine license SHA-256 mismatch. Expected $($manifest.licenseSha256), got $actualLicenseHash."
}

if (-not (Test-Path -LiteralPath $targetPath)) {
    Invoke-WebRequest -Uri $manifest.downloadUri -OutFile $targetPath -UseBasicParsing
}

$actualHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
if ($actualHash -ne $manifest.sha256) {
    throw "Sunshine payload SHA-256 mismatch. Expected $($manifest.sha256), got $actualHash."
}

$signature = Get-AuthenticodeSignature -LiteralPath $targetPath
if ($signature.Status -ne 'Valid') {
    throw "Sunshine payload signature is not valid: $($signature.Status)"
}

if ($signature.SignerCertificate.Subject -ne $manifest.expectedSignerSubject) {
    throw "Unexpected Sunshine signer: $($signature.SignerCertificate.Subject)"
}

if ($signature.SignerCertificate.Thumbprint -ne $manifest.expectedSignerThumbprint) {
    throw "Unexpected Sunshine signer certificate thumbprint: $($signature.SignerCertificate.Thumbprint)"
}

[pscustomobject]@{
    Path = (Resolve-Path -LiteralPath $targetPath).Path
    Version = $manifest.version
    SHA256 = $actualHash
    Signer = $signature.SignerCertificate.Subject
    License = $manifest.licenseExpression
    Source = $manifest.sourceUri
}
