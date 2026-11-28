[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$MsysRoot,
    [string]$OutputDirectory,
    [string]$FfmpegDirectory,
    [switch]$ConfigureOnly,
    [switch]$PublicPreview
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
if ($FfmpegDirectory) {
    $FfmpegDirectory = (Resolve-Path -LiteralPath $FfmpegDirectory -ErrorAction Stop).Path.Replace('\', '/')
}
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
    $configureFlags = @()
    if ($PublicPreview) {
        # GCC reports the physical include directory even when MSYS2 is reached via a junction.
        $toolchain = Get-Item -LiteralPath $MsysRoot
        $physicalRoot = if ($toolchain.LinkType) { $toolchain.ResolveLinkTarget($true).FullName } else { $toolchain.FullName }
        $mappings = foreach ($entry in @(@($physicalRoot, '/toolchain'), @($sourcePath, '/source'))) {
            '"-ffile-prefix-map=' + $entry[0].Replace('\', '/') + '=' + $entry[1] + '"'
        }
        $gccFlags = $mappings -join ' '
        $configureFlags = @("-DCMAKE_C_FLAGS=$gccFlags", "-DCMAKE_CXX_FLAGS=$gccFlags")
    }
    $brandAssets = Join-Path $repoRoot 'assets/brand/exports'
    $engineIcon = (Resolve-Path -LiteralPath (Join-Path $brandAssets 'windows/piulink-host.ico')).Path.Replace('\', '/')
    # Do not carry an SDK from an earlier custom build into the next default build.
    $configureFlags += if ($FfmpegDirectory) { "-DFFMPEG_PREPARED_BINARIES:PATH=$FfmpegDirectory" } else { '-UFFMPEG_PREPARED_BINARIES' }
    & $cmake -S $sourcePath -B $buildPath -G Ninja @configureFlags -DBUILD_DOCS=OFF -DBUILD_TESTS=OFF -DSUNSHINE_ENABLE_TRAY=OFF -DSUNSHINE_PUBLISHER_NAME=PiuLink "-DSUNSHINE_ICON_PATH=$engineIcon"
    if ($LASTEXITCODE) { throw 'Engine configure failed.' }
    if ($ConfigureOnly) { return }
    & $cmake --build $buildPath --parallel 8
    if ($LASTEXITCODE) { throw 'Engine build failed.' }
    & $cmake --install $buildPath --prefix $outputPath
    if ($LASTEXITCODE) { throw 'Engine staging failed.' }
    # The Rust builder requires its source overlay to live outside the input tree.
    $rustOutput = "$outputPath-rust-build"
    $python = if ($env:PYTHON) { $env:PYTHON } else { 'python' }
    & $python (Join-Path $repoRoot 'engines/rust/integration/sunshine/build.py') $sourcePath (Join-Path $MsysRoot 'ucrt64/bin') --output $rustOutput
    if ($LASTEXITCODE) { throw 'Rust Host media build failed.' }
    Move-Item -LiteralPath (Join-Path $outputPath 'sunshine.exe') -Destination (Join-Path $outputPath 'PiuLink.Engine.exe') -Force
    Copy-Item -LiteralPath (Join-Path $rustOutput 'sunshine.exe') -Destination (Join-Path $outputPath 'PiuLink.Engine.exe') -Force
    Copy-Item -LiteralPath (Join-Path $rustOutput 'piulink_nvenc_rate.dll') -Destination $outputPath -Force
    Move-Item -LiteralPath (Join-Path $outputPath 'tools/sunshinesvc.exe') -Destination (Join-Path $outputPath 'tools/PiuLink.Engine.Service.exe') -Force
    $diagnosticManifest = Join-Path $repoRoot 'engines/rust/integration/dxgi-info/Cargo.toml'
    $diagnosticTarget = Join-Path $buildPath 'piulink-dxgi'
    # The standalone Host must run this utility on Windows without a VC++ redistributable.
    $previousRustFlags = $env:RUSTFLAGS
    try {
        $env:RUSTFLAGS = "$previousRustFlags -Ctarget-feature=+crt-static".Trim()
        & cargo build --release --locked --target x86_64-pc-windows-msvc --manifest-path $diagnosticManifest --target-dir $diagnosticTarget
        if ($LASTEXITCODE) { throw 'Rust DXGI diagnostic build failed.' }
    } finally {
        $env:RUSTFLAGS = $previousRustFlags
    }
    Copy-Item -LiteralPath (Join-Path $diagnosticTarget 'x86_64-pc-windows-msvc/release/dxgi-info.exe') -Destination (Join-Path $outputPath 'tools/dxgi-info.exe') -Force
    $images = Join-Path $outputPath 'assets/web/images'
    $web = Split-Path -Parent $images
    & "$repoRoot/apps/windows-host/installers/Stage-WebBrand.ps1" -WebRoot $web -OutputDirectory $web
    Get-ChildItem -LiteralPath (Join-Path $brandAssets 'windows/engine-tray') -File | Copy-Item -Destination $images -Force
    Copy-Item -LiteralPath $engineIcon -Destination (Join-Path $images 'sunshine.ico') -Force
    foreach ($size in 16, 45) {
        Copy-Item -LiteralPath (Join-Path $brandAssets "web/piulink-$size.png") -Destination (Join-Path $images "logo-sunshine-$size.png") -Force
    }
    & "$PSScriptRoot/Save-EngineSymbols.ps1" -EngineDirectory $outputPath -MsysRoot $MsysRoot
    $pin | Add-Member -Force NoteProperty rustEngineSha256 ((Get-FileHash (Join-Path $outputPath $pin.windowsExecutable)).Hash.ToLowerInvariant())
    $pin | Add-Member -Force NoteProperty rustDllSha256 ((Get-FileHash (Join-Path $outputPath 'piulink_nvenc_rate.dll')).Hash.ToLowerInvariant())
    $pin | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $outputPath 'piulink-engine.json')
    Write-Output "Development engine staged at $outputPath. No installed service was changed."
} finally {
    Pop-Location
    $env:PATH = $previousPath
    foreach ($name in $buildEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $buildEnvironment[$name], 'Process')
    }
}
