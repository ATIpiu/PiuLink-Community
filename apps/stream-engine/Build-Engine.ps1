[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$MsysRoot,
    [string]$OutputDirectory,
    [switch]$ConfigureOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'upstream.json') -Raw | ConvertFrom-Json
if (!$SourceDirectory) { $SourceDirectory = Join-Path $repoRoot 'work/piulink-engine-source' }
if (!$MsysRoot) { $MsysRoot = Join-Path $repoRoot 'work/sunshine-toolchain/msys64' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts/stream-engine-$($pin.engineVersion)" }
$sourcePath = [IO.Path]::GetFullPath($SourceDirectory)
$MsysRoot = [IO.Path]::GetFullPath($MsysRoot)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$cmake = Join-Path $MsysRoot 'ucrt64/bin/cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { throw 'Install the upstream MSYS2 UCRT64 build dependencies first; see README.md.' }
if (!(Test-Path -LiteralPath $sourcePath)) {
    & git clone --no-checkout $pin.repository $sourcePath
    if ($LASTEXITCODE) { throw 'Engine source clone failed.' }
    & git -C $sourcePath checkout --detach $pin.commit
    if ($LASTEXITCODE) { throw 'Pinned engine checkout failed.' }
}
$head = & git -C $sourcePath rev-parse HEAD
if ($LASTEXITCODE -or $head -ne $pin.commit) { throw 'Existing source checkout does not match the engine pin. It was not changed.' }
. (Join-Path $PSScriptRoot "PatchSet.ps1")
Sync-PiuLinkPatchSet -Source $sourcePath -Patches @($pin.patches | ForEach-Object { Join-Path $PSScriptRoot $_ })
$submodulePath = $env:PATH
try {
    # Git for Windows invokes shell helpers such as basename and sed for submodules.
    # Make the pinned MSYS runtime visible before the main CMake environment is set.
    $env:PATH = "$(Join-Path $MsysRoot 'usr/bin');$submodulePath"
    & git -C $sourcePath submodule update --init --recursive
    if ($LASTEXITCODE) { throw 'Pinned submodule checkout failed.' }
}
finally {
    $env:PATH = $submodulePath
}
foreach ($group in ($pin.dependencyPatches | Group-Object directory)) {
    Sync-PiuLinkPatchSet -Source (Join-Path $sourcePath $group.Name) -Patches @($group.Group | ForEach-Object { Join-Path $PSScriptRoot $_.patch })
}

$buildPath = Join-Path $sourcePath 'build-piulink'
$previousPath = $env:PATH
$buildEnvironment = @{}
foreach ($name in @('BRANCH', 'BUILD_VERSION', 'COMMIT', 'CLONE_URL')) {
    $buildEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location -LiteralPath $sourcePath
try {
    $env:PATH = "$(Join-Path $MsysRoot 'ucrt64/bin');$(Join-Path $MsysRoot 'usr/bin');$previousPath"
    $env:BRANCH = 'piulink'
    $env:BUILD_VERSION = $pin.tag.TrimStart('v')
    $env:COMMIT = $pin.commit
    $env:CLONE_URL = $pin.repository
    & $cmake -S $sourcePath -B $buildPath -G Ninja -DBUILD_DOCS=OFF -DBUILD_TESTS=OFF -DSUNSHINE_ENABLE_TRAY=OFF -DSUNSHINE_PUBLISHER_NAME=PiuLink
    if ($LASTEXITCODE) { throw 'Engine configure failed.' }
    if ($ConfigureOnly) { return }
    & $cmake --build $buildPath --parallel 8
    if ($LASTEXITCODE) { throw 'Engine build failed.' }
    & $cmake --install $buildPath --prefix $outputPath
    if ($LASTEXITCODE) { throw 'Engine staging failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upstream.json') -Destination (Join-Path $outputPath 'piulink-engine.json')
    Write-Output "Development engine staged at $outputPath. No installed service was changed."
} finally {
    Pop-Location
    $env:PATH = $previousPath
    foreach ($name in $buildEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $buildEnvironment[$name], 'Process')
    }
}
