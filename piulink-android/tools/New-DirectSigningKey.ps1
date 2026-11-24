[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\secrets\direct-signing')
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$keyStorePath = Join-Path $resolvedOutput 'piulink-direct-release.keystore'
$configurationPath = Join-Path $resolvedOutput 'signing.json'
if ((Test-Path -LiteralPath $keyStorePath) -or (Test-Path -LiteralPath $configurationPath)) {
    throw "A direct-distribution signing identity already exists at $resolvedOutput. Refusing to overwrite it."
}

$keyTool = (Get-Command keytool.exe -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$storePassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$keyPassword = $storePassword
$alias = 'piulink-direct-release'

& $keyTool -genkeypair -noprompt `
    -keystore $keyStorePath `
    -storetype PKCS12 `
    -storepass $storePassword `
    -keypass $keyPassword `
    -alias $alias `
    -keyalg RSA `
    -keysize 4096 `
    -sigalg SHA256withRSA `
    -validity 10000 `
    -dname 'CN=PiuLink Direct Distribution, O=ATIPiu Labs, C=CN'
if ($LASTEXITCODE -ne 0) {
    throw "keytool failed with exit code $LASTEXITCODE."
}

$configuration = [ordered]@{
    storeFile = $keyStorePath
    storePassword = $storePassword
    keyAlias = $alias
    keyPassword = $keyPassword
} | ConvertTo-Json
[IO.File]::WriteAllText($configurationPath, $configuration)
Write-Output "Created the stable direct-distribution Android signing identity at $resolvedOutput"
Write-Output 'Back up the keystore and signing.json together. Losing them makes future APK updates impossible.'
