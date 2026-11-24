[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '../../work/piulink-engine-source'),
    [string]$MsysRoot = (Join-Path $PSScriptRoot '../../work/sunshine-toolchain/msys64')
)

$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($SourceDirectory)
$compiler = Join-Path $MsysRoot 'ucrt64/bin/g++.exe'
$testSource = Join-Path $PSScriptRoot 'tests/content_adaptive_rate_test.cpp'
$testBinary = Join-Path ([IO.Path]::GetTempPath()) 'piulink-content-rate-test.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw "Compiler not found: $compiler" }
if (!(Test-Path -LiteralPath (Join-Path $sourcePath 'src/nvenc/piulink_content_rate.h'))) {
    throw 'Apply the PiuLink engine patches before running this test.'
}

$nvencSource = Get-Content -LiteralPath (Join-Path $sourcePath 'src/nvenc/nvenc_base.cpp') -Raw
$streamSource = Get-Content -LiteralPath (Join-Path $sourcePath 'src/stream.cpp') -Raw
if ($streamSource -notmatch 'value < 500 \|\| value > 150000') {
    throw 'Host live bitrate control must allow auto mode to grow beyond its initial bitrate.'
}
$reconfigureBody = [regex]::Match(
    $nvencSource,
    'bool nvenc_base::apply_bitrate\(uint32_t kbps\) \{(?<body>.*?)\n  \}',
    [Text.RegularExpressions.RegexOptions]::Singleline)
if (!$reconfigureBody.Success -or
        $reconfigureBody.Groups['body'].Value -notmatch 'params\.resetEncoder\s*=\s*1' -or
        $reconfigureBody.Groups['body'].Value -notmatch 'params\.forceIDR\s*=\s*1') {
    throw 'NVENC live bitrate reconfiguration must reset rate control and force an IDR frame.'
}

$previousPath = $env:PATH
try {
    $env:PATH = "$(Join-Path $MsysRoot 'ucrt64/bin');$(Join-Path $MsysRoot 'usr/bin');$previousPath"
    & $compiler -std=c++17 -Wall -Wextra -Werror "-I$(Join-Path $sourcePath 'src/nvenc')" $testSource -o $testBinary
    if ($LASTEXITCODE) { throw 'Content-adaptive rate policy compilation failed.' }
    & $testBinary
    if ($LASTEXITCODE) { throw 'Content-adaptive rate policy test failed.' }
    Write-Output 'Content-adaptive rate policy test passed.'
} finally {
    $env:PATH = $previousPath
    Remove-Item -LiteralPath $testBinary -Force -ErrorAction SilentlyContinue
}
