[CmdletBinding()]
param([switch]$Android)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $PSScriptRoot
try {
    $out=Join-Path $repo 'work/direct-tunnel-build'
    New-Item -ItemType Directory -Force $out | Out-Null
    if (!$Android) {
        go build -trimpath -buildvcs=false -ldflags '-s -w' -o "$out/piulink-direct.exe" ./cmd/piulink-tunnel
        if ($LASTEXITCODE) {throw 'Direct transport build failed'}
        return
    }
    $sdkLine=(Get-Content "$repo/piulink-android/local.properties" | Where-Object {$_ -like 'sdk.dir=*'} | Select-Object -First 1)
    if ($sdkLine) {$env:ANDROID_HOME=$sdkLine.Substring(8).Replace('\:',':').Replace('\\','\')}
    $env:ANDROID_NDK_HOME=Join-Path $env:ANDROID_HOME 'ndk/27.0.12077973'
    $env:GOBIN=$out
    $env:PATH="$out;$env:PATH"
    foreach ($name in 'gobind','gomobile') {
        if (!(Test-Path "$out/$name.exe")) {
            go install "golang.org/x/mobile/cmd/${name}@v0.0.0-20260818145002-f020ddb2de58"
            if ($LASTEXITCODE) {throw "Failed to install $name"}
        }
    }
    # anet's Android interface enumeration uses the Go linker option documented by that dependency.
    & "$out/gomobile.exe" bind -trimpath -target=android -androidapi 26 '-ldflags=-checklinkname=0 -s -w' -o "$out/piulink-direct.aar" ./mobile
    if ($LASTEXITCODE) {throw 'Direct Android transport build failed'}
    New-Item -ItemType Directory -Force "$repo/piulink-android/app/libs" | Out-Null
    Copy-Item -LiteralPath "$out/piulink-direct.aar" -Destination "$repo/piulink-android/app/libs/piulink-direct.aar" -Force
} finally { Pop-Location }
