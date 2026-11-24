[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\work\moonlight-qt'),
    [string]$QtRoot = 'C:\Qt',
    [string]$BuildToolsRoot = 'C:\BuildTools',
    [string]$OutputDirectory,
    [switch]$SkipDeploy
)

$ErrorActionPreference = 'Stop'
$engineRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$clientRoot = [IO.Path]::GetFullPath((Join-Path $engineRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $clientRoot '..\..'))
$sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $engineRoot 'upstream.json') -Raw | ConvertFrom-Json
$qtBin = Join-Path $QtRoot "$($manifest.qtVersion)\$($manifest.qtArchitecture)\bin"
$qmake = Join-Path $qtBin 'qmake.exe'
$deployQt = Join-Path $qtBin 'windeployqt.exe'
$vcvars = Join-Path $BuildToolsRoot 'VC\Auxiliary\Build\vcvarsall.bat'

foreach ($required in @($sourceRoot, $qmake, $vcvars)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required build input was not found: $required" }
}

$actualSource = (git -C $sourceRoot rev-parse HEAD).Trim()
if ($actualSource -ne $manifest.upstreamCommit) {
    throw "Moonlight source mismatch. Expected $($manifest.upstreamCommit), got $actualSource."
}
. (Join-Path $PSScriptRoot '../../stream-engine/PatchSet.ps1')
Sync-PiuLinkPatchSet -Source $sourceRoot -Patches @($manifest.patches | ForEach-Object { Join-Path $engineRoot $_ })
foreach ($group in ($manifest.dependencyPatches | Group-Object directory)) {
    Sync-PiuLinkPatchSet -Source (Join-Path $sourceRoot $group.Name) -Patches @($group.Group | ForEach-Object { Join-Path $engineRoot $_.patch })
}

$buildRoot = Join-Path $sourceRoot 'build\piulink-x64-release'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$qmakeProject = Join-Path $sourceRoot 'moonlight-qt.pro'
$jom = Join-Path $sourceRoot 'scripts\jom.exe'
$nativeCommand = "call `"$vcvars`" amd64 && cd /d `"$buildRoot`" && `"$qmake`" `"$qmakeProject`" && `"$jom`" release"
& cmd.exe /d /s /c $nativeCommand
if ($LASTEXITCODE -ne 0) { throw "PiuLink Moonlight native build failed with exit code $LASTEXITCODE." }

$builtExe = Join-Path $buildRoot 'app\release\Moonlight.exe'
if (-not (Test-Path -LiteralPath $builtExe)) { throw "Native build did not produce $builtExe" }
$antiHookingDll = Join-Path $buildRoot 'AntiHooking\release\AntiHooking.dll'
if (-not (Test-Path -LiteralPath $antiHookingDll)) { throw "Native build did not produce $antiHookingDll" }
if ($SkipDeploy) { Write-Output $builtExe; return }

$versionLabel = "$($manifest.upstreamRelease.TrimStart('v'))-piulink.$($manifest.piulinkRevision)"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\windows-engine\PiuLink-Moonlight-$versionLabel-x64"
}
$deployRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $deployRoot.StartsWith(($repoRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must remain inside the repository: $deployRoot"
}
if (Test-Path -LiteralPath $deployRoot) { Remove-Item -LiteralPath $deployRoot -Recurse -Force }
New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null
Copy-Item -LiteralPath $builtExe -Destination (Join-Path $deployRoot 'PiuLink-Moonlight.exe')
Copy-Item -LiteralPath $antiHookingDll -Destination $deployRoot
Copy-Item -Path (Join-Path $sourceRoot 'libs\windows\lib\x64\*.dll') -Destination $deployRoot
Copy-Item -LiteralPath (Join-Path $sourceRoot 'app\SDL_GameControllerDB\gamecontrollerdb.txt') -Destination $deployRoot
& $deployQt --dir $deployRoot --release --qmldir (Join-Path $sourceRoot 'app\gui') `
    --no-opengl-sw --no-sql --no-system-d3d-compiler --no-system-dxc-compiler `
    --skip-plugin-types qmltooling,generic --no-ffmpeg $builtExe
if ($LASTEXITCODE -ne 0) { throw "Qt runtime deployment failed with exit code $LASTEXITCODE." }

# windeployqt only discovers the compiler runtime when it inherits a Visual Studio
# developer environment. Keep deployment deterministic by staging the matching
# redistributable runtime directly from the Build Tools installation.
$vcRedistRoot = Join-Path $BuildToolsRoot 'VC\Redist\MSVC'
$vcRuntimeDirectory = Get-ChildItem -LiteralPath $vcRedistRoot -Directory |
    Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $vcRuntimeDirectory) {
    throw "Visual C++ x64 runtime was not found under $vcRedistRoot."
}
Copy-Item -Path (Join-Path $vcRuntimeDirectory '*.dll') -Destination $deployRoot -Force

Copy-Item -LiteralPath (Join-Path $engineRoot 'upstream.json') -Destination (Join-Path $deployRoot 'PiuLink-Engine-Manifest.json')
Copy-Item -LiteralPath (Join-Path $clientRoot 'installers\licenses\Moonlight-NOTICE.md') -Destination $deployRoot
Write-Output "Built PiuLink Moonlight $versionLabel at $deployRoot"
