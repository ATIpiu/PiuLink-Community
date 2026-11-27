[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\work\moonlight-qt'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$engineRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$clientRoot = [IO.Path]::GetFullPath((Join-Path $engineRoot '..'))
$destinationPath = [IO.Path]::GetFullPath($Destination)
$manifest = Get-Content -LiteralPath (Join-Path $engineRoot 'upstream.json') -Raw | ConvertFrom-Json

if (-not $destinationPath.StartsWith(($clientRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Destination must be inside the Windows client tree: $destinationPath"
}

if (Test-Path -LiteralPath $destinationPath) {
    if (-not $Force) {
        throw "Source directory already exists: $destinationPath (use -Force to recreate it)"
    }
    Remove-Item -LiteralPath $destinationPath -Recurse -Force
}

git clone --filter=blob:none --no-checkout $manifest.upstreamRepository $destinationPath
if ($LASTEXITCODE -ne 0) { throw 'Unable to clone Moonlight Qt source.' }
git -C $destinationPath checkout --detach $manifest.upstreamCommit
if ($LASTEXITCODE -ne 0) { throw 'Unable to check out pinned Moonlight Qt commit.' }
git -C $destinationPath submodule update --init --recursive
if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize Moonlight Qt submodules.' }

$actualCommon = (git -C (Join-Path $destinationPath 'moonlight-common-c\moonlight-common-c') rev-parse HEAD).Trim()
if ($actualCommon -ne $manifest.moonlightCommonCommit) {
    throw "moonlight-common-c mismatch. Expected $($manifest.moonlightCommonCommit), got $actualCommon."
}

function Apply-PinnedPatch([string]$Root, [string]$RelativePatch) {
    $patch = [IO.Path]::GetFullPath((Join-Path $engineRoot $RelativePatch))
    if (-not $patch.StartsWith(($engineRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Patch is outside the engine tree: $RelativePatch"
    }
    if (-not (Test-Path -LiteralPath $patch -PathType Leaf)) { throw "Missing pinned patch: $patch" }
    git -C $Root apply --check $patch
    if ($LASTEXITCODE -ne 0) { throw "Patch validation failed: $RelativePatch" }
    git -C $Root apply $patch
    if ($LASTEXITCODE -ne 0) { throw "Patch application failed: $RelativePatch" }
}

foreach ($relativePatch in $manifest.patches) {
    Apply-PinnedPatch $destinationPath $relativePatch
}
foreach ($dependency in $manifest.dependencyPatches) {
    $dependencyRoot = [IO.Path]::GetFullPath((Join-Path $destinationPath $dependency.directory))
    if (-not $dependencyRoot.StartsWith(($destinationPath.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Dependency is outside the checked-out tree: $($dependency.directory)"
    }
    Apply-PinnedPatch $dependencyRoot $dependency.patch
}
# The integrated patch already owns piulinkbridge.cpp/.h. The retired overlay is
# intentionally NOT copied: it would erase virtual-gamepad command support.

Write-Output "Prepared PiuLink Moonlight Qt $($manifest.upstreamRelease)-piulink.$($manifest.piulinkRevision) source at $destinationPath"
