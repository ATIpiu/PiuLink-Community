[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [string]$EngineDirectory,
    [string]$NativeEngineDirectory,
    [string]$QtEngineDirectory,
    [ValidateSet('Standalone', 'Compact')]
    [string]$DistributionMode = 'Standalone',
    [switch]$ChineseEnglishOnly,
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$clientRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $clientRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\windows-client\$Runtime"
}
if ($EngineDirectory) {
    $hasQt = Test-Path -LiteralPath (Join-Path $EngineDirectory 'PiuLink-Moonlight.exe')
    $hasNative = Test-Path -LiteralPath (Join-Path $EngineDirectory 'PiuLink.Native.dll')
    if (!$hasQt -and !$hasNative) { throw 'EngineDirectory must contain the Qt executable or the native streaming DLL.' }
    if ($hasQt -and !$QtEngineDirectory) { $QtEngineDirectory = $EngineDirectory }
    if ($hasNative -and !$NativeEngineDirectory) { $NativeEngineDirectory = $EngineDirectory }
}
if ([string]::IsNullOrWhiteSpace($NativeEngineDirectory)) {
    $NativeEngineDirectory = Join-Path $repoRoot 'artifacts\windows-engine\PiuLink-Native-0.1.0-x64'
}
if ([string]::IsNullOrWhiteSpace($QtEngineDirectory)) {
    $revision = (Get-Content -LiteralPath (Join-Path $clientRoot 'engine\upstream.json') -Raw | ConvertFrom-Json).piulinkRevision
    $QtEngineDirectory = Join-Path $repoRoot "artifacts\windows-engine\PiuLink-Moonlight-6.1.0-piulink.$revision-x64"
}
$resolvedNativeEngine = [IO.Path]::GetFullPath($NativeEngineDirectory)
$resolvedQtEngine = [IO.Path]::GetFullPath($QtEngineDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $resolvedNativeEngine 'PiuLink.Native.dll'))) {
    throw "PiuLink native streaming payload is missing: $resolvedNativeEngine. Run native-engine\Build-NativeEngine.ps1 first."
}
if (-not (Test-Path -LiteralPath (Join-Path $resolvedQtEngine 'PiuLink-Moonlight.exe'))) {
    throw "Qt Moonlight payload is missing: $resolvedQtEngine. Run engine\Build-Engine.ps1 first."
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$resolvedRepo = $repoRoot.TrimEnd('\') + '\'
if (-not $resolvedOutput.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must remain inside the PiuLink repository: $resolvedOutput"
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$selfContained = if ($DistributionMode -eq 'Standalone') { 'true' } else { 'false' }
& $dotnet publish (Join-Path $clientRoot 'src\PiuLink.Client\PiuLink.Client.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContained `
    --output $resolvedOutput `
    -p:PiuLinkNativeEngineDirectory=$resolvedNativeEngine `
    -p:PiuLinkQtEngineDirectory=$resolvedQtEngine `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "PiuLink Client publish failed with exit code $LASTEXITCODE."
}

& $dotnet publish (Join-Path $clientRoot 'src\PiuLink.Client.Cli\PiuLink.Client.Cli.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContained `
    --output $resolvedOutput `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "PiuLink Client CLI publish failed with exit code $LASTEXITCODE."
}

# PiuLink.Native.dll is built with the dynamic MSVC runtime. Keep the portable
# distribution truly standalone by staging the matching x64 CRT beside it.
$nativeOutput = Join-Path $resolvedOutput 'native'
$vcRedistRoot = 'C:\BuildTools\VC\Redist\MSVC'
$vcRuntimeDirectory = Get-ChildItem -LiteralPath $vcRedistRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $vcRuntimeDirectory) { throw "Visual C++ x64 runtime was not found under $vcRedistRoot." }
Copy-Item -Path (Join-Path $vcRuntimeDirectory '*.dll') -Destination $nativeOutput -Force

# The self-contained runtime publishes its optional crash-dump helper by default.
# PiuLink Client does not invoke it.
$createDumpPath = Join-Path $resolvedOutput 'createdump.exe'
if (Test-Path -LiteralPath $createDumpPath) {
    Remove-Item -LiteralPath $createDumpPath -Force
}

if ($ChineseEnglishOnly) {
    $translationDirectory = Join-Path $resolvedOutput 'moonlight-qt\translations'
    if (Test-Path -LiteralPath $translationDirectory) {
        Get-ChildItem -LiteralPath $translationDirectory -Filter '*.qm' -File |
            Where-Object { $_.Name -notin @('qt_en.qm', 'qt_zh_CN.qm', 'qt_zh_TW.qm') } |
            Remove-Item -Force
    }
}

Write-Output "PiuLink Client $DistributionMode build published to $resolvedOutput"

if (-not $SkipArchive) {
    $modeSuffix = if ($DistributionMode -eq 'Compact') { '-compact' } else { '' }
    $projectXml = [xml][IO.File]::ReadAllText((Join-Path $clientRoot 'src\PiuLink.Client\PiuLink.Client.csproj'))
    $clientVersion = [string]$projectXml.Project.PropertyGroup.Version
    $archivePath = Join-Path (Split-Path -Parent $resolvedOutput) "PiuLink.Client-$clientVersion$modeSuffix-$Runtime.zip"
    $resolvedArchive = [IO.Path]::GetFullPath($archivePath)
    if (-not $resolvedArchive.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Archive path must remain inside the PiuLink repository: $resolvedArchive"
    }

    if (Test-Path -LiteralPath $resolvedArchive) {
        Remove-Item -LiteralPath $resolvedArchive -Force
    }

    Compress-Archive -Path (Join-Path $resolvedOutput '*') -DestinationPath $resolvedArchive -CompressionLevel Optimal
    Write-Output "Portable archive created at $resolvedArchive"
}
