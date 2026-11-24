[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$IdentityName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^CN=')]
    [string]$Publisher,

    [ValidateNotNullOrEmpty()]
    [string]$PublisherDisplayName = 'ATIPiu Labs',

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.7.0',

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$scriptDirectory = Split-Path -Parent $PSCommandPath
$hostRoot = (Resolve-Path (Join-Path $scriptDirectory '..\..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $hostRoot '..\..')).Path
$hostProject = Join-Path $hostRoot 'src\PiuLink.Host\PiuLink.Host.csproj'
$manifestTemplate = Join-Path $scriptDirectory 'AppxManifest.template.xml'
$sunshinePayload = Join-Path $hostRoot 'installers\cache\Sunshine-Windows-AMD64-installer.msi'
$packagingToolsProject = Join-Path $scriptDirectory 'StorePackagingTools.csproj'

if (-not (Test-Path -LiteralPath $sunshinePayload)) {
    throw "The verified Sunshine payload is missing. Run installers\Acquire-Sunshine.ps1 first."
}

$dotnet = Join-Path $env:ProgramW6432 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'The .NET SDK could not be found.'
    }
    $dotnet = $dotnetCommand.Source
}

$windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -LiteralPath $windowsKitsRoot -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -match '\\x64$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $makeAppx) {
    & $dotnet restore $packagingToolsProject --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "The Windows SDK BuildTools restore failed with exit code $LASTEXITCODE."
    }
    $nugetToolsRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    $makeAppx = Get-ChildItem -LiteralPath $nugetToolsRoot -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match '\\x64$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
}
if ($null -eq $makeAppx) {
    throw 'MakeAppx.exe was not found in the installed Windows SDK or restored Microsoft BuildTools package.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\store'
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$workRoot = Join-Path $repositoryRoot 'work\store-msix'
$publishRoot = Join-Path $workRoot 'package'

if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

& $dotnet publish $hostProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "The PiuLink Host publish failed with exit code $LASTEXITCODE."
}

$assetDirectory = Join-Path $publishRoot 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $hostRoot 'store-assets\StoreLogo.scale-100.png') `
    -Destination (Join-Path $assetDirectory 'StoreLogo.png')
Copy-Item -LiteralPath (Join-Path $hostRoot 'store-assets\Square150x150Logo.scale-100.png') `
    -Destination (Join-Path $assetDirectory 'Square150x150Logo.png')
Copy-Item -LiteralPath (Join-Path $hostRoot 'store-assets\Square44x44Logo.scale-100.png') `
    -Destination (Join-Path $assetDirectory 'Square44x44Logo.png')

$manifest = Get-Content -LiteralPath $manifestTemplate -Raw
$manifest = $manifest.Replace('__IDENTITY_NAME__', [Security.SecurityElement]::Escape($IdentityName))
$manifest = $manifest.Replace('__PUBLISHER__', [Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', [Security.SecurityElement]::Escape($PublisherDisplayName))
$manifest = $manifest.Replace('__VERSION__', $Version)
Set-Content -LiteralPath (Join-Path $publishRoot 'AppxManifest.xml') -Value $manifest -Encoding utf8

$packagePath = Join-Path $outputRoot "PiuLink-$Version-x64.msix"
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}
& $makeAppx.FullName pack /d $publishRoot /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE."
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    Package = $packagePath
    SHA256 = $hash
    IdentityName = $IdentityName
    Publisher = $Publisher
    Version = $Version
    Signed = $false
    IntendedDistribution = 'Microsoft Store submission only'
} | ConvertTo-Json
