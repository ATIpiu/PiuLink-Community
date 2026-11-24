[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '../../../work/piulink-engine-source'),
    [string]$MsysRoot = (Join-Path $PSScriptRoot '../../../work/sunshine-toolchain/msys64'),
    [string]$Ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source,
    [string]$Ffprobe = (Get-Command ffprobe.exe -ErrorAction Stop).Source,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../../artifacts/benchmarks/content-adaptive'),
    [int]$Width = 1280,
    [int]$Height = 720,
    [int]$Fps = 60,
    [int]$SecondsPerPhase = 5,
    [int]$CeilingKbps = 20000,
    [ValidateSet('h264_nvenc', 'hevc_nvenc', 'av1_nvenc', 'libx264', 'libx265')][string]$Encoder = 'h264_nvenc'
)

$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($SourceDirectory)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $MsysRoot 'ucrt64/bin/g++.exe'
$header = Join-Path $sourcePath 'src/nvenc/piulink_content_rate.h'
foreach ($required in @($compiler, $header, $Ffmpeg, $Ffprobe)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Benchmark dependency not found: $required" }
}
if ($Width -lt 320 -or $Height -lt 240 -or $Fps -lt 10 -or $SecondsPerPhase -lt 2 -or $CeilingKbps -lt 1000) {
    throw 'Benchmark dimensions, frame rate, phase duration, or bitrate are outside the supported range.'
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$policyExe = Join-Path $outputPath 'content-adaptive-policy-benchmark.exe'
$policyCsv = Join-Path $outputPath 'policy-transitions.csv'
$policyJson = Join-Path $outputPath 'policy-summary.json'
$testSource = Join-Path $PSScriptRoot 'content_adaptive_policy_benchmark.cpp'

$previousPath = $env:PATH
try {
    $env:PATH = "$(Join-Path $MsysRoot 'ucrt64/bin');$(Join-Path $MsysRoot 'usr/bin');$previousPath"
    & $compiler -std=c++17 -O2 -Wall -Wextra -Werror "-I$(Split-Path $header)" $testSource -o $policyExe
    if ($LASTEXITCODE) { throw 'Policy benchmark compilation failed.' }
    $policyOutput = & $policyExe $CeilingKbps $Fps 2>&1
    $policyOutput | Where-Object { $_ -notmatch '^\{' } | Set-Content -LiteralPath $policyCsv -Encoding utf8
    $policyOutput | Where-Object { $_ -match '^\{' } | Select-Object -Last 1 | Set-Content -LiteralPath $policyJson -Encoding utf8
} finally { $env:PATH = $previousPath }

$reference = Join-Path $outputPath 'reference.mkv'
$baseline = Join-Path $outputPath 'baseline.mp4'
$adaptive = Join-Path $outputPath 'adaptive.mp4'
$floorKbps = [Math]::Min($CeilingKbps, [Math]::Max(4000, [Math]::Floor($CeilingKbps * 0.60)))
$phaseFilters = @(
    "testsrc2=s=${Width}x${Height}:r=${Fps}:d=$SecondsPerPhase",
    "color=c=0x20242a:s=${Width}x${Height}:r=${Fps}:d=$SecondsPerPhase",
    "testsrc2=s=${Width}x${Height}:r=${Fps}:d=$SecondsPerPhase",
    "smptebars=s=${Width}x${Height}:r=${Fps}:d=$SecondsPerPhase"
)
$inputs = @(); $normalized = @(); $concat = @(); $framesPerPhase = $Fps * $SecondsPerPhase
for ($i = 0; $i -lt $phaseFilters.Count; $i++) {
    $inputs += @('-f','lavfi','-i',$phaseFilters[$i])
    $normalized += "[$i`:v]trim=end_frame=$framesPerPhase,setpts=PTS-STARTPTS[p$i];"
    $concat += "[p$i]"
}
& $Ffmpeg -hide_banner -loglevel error -y @inputs -filter_complex "$($normalized -join '')$($concat -join '')concat=n=4:v=1:a=0,format=yuv420p[v]" -map '[v]' -c:v ffv1 $reference
if ($LASTEXITCODE) { throw 'Reference workload generation failed.' }

$encoderArgs = if ($Encoder.EndsWith('_nvenc')) {
    @('-preset','p4','-tune','ll','-rc','cbr','-g',($Fps * 2))
} else {
    @('-preset','medium','-tune','zerolatency','-g',($Fps * 2))
}
function Encode([string]$sourceFile, [string]$output, [int]$kbps) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $Ffmpeg -hide_banner -loglevel error -y -i $sourceFile -an -c:v $Encoder @encoderArgs -b:v "${kbps}k" -maxrate "${kbps}k" -bufsize "$($kbps * 2)k" $output
    if ($LASTEXITCODE) { throw "Encoding failed: $output" }
    $watch.Stop(); return $watch.Elapsed.TotalSeconds
}
$baselineSeconds = Encode $reference $baseline $CeilingKbps

$adaptiveParts = @(); $offset = 0; $adaptiveEncodeSeconds = 0.0
for ($i = 0; $i -lt 4; $i++) {
    $partSource = Join-Path $outputPath "source-$i.mkv"
    $partOutput = Join-Path $outputPath "adaptive-$i.mp4"
    & $Ffmpeg -hide_banner -loglevel error -y -ss $offset -t $SecondsPerPhase -i $reference -c:v ffv1 $partSource
    $target = if ($i -in 0,2) { $CeilingKbps } else { $floorKbps }
    $adaptiveEncodeSeconds += Encode $partSource $partOutput $target
    $adaptiveParts += $partOutput
    $offset += $SecondsPerPhase
}
$concatFile = Join-Path $outputPath 'adaptive-parts.txt'
$adaptiveParts | ForEach-Object { "file '$($_ -replace "'", "''")'" } | Set-Content -LiteralPath $concatFile -Encoding ascii
$adaptiveWatch = [Diagnostics.Stopwatch]::StartNew()
& $Ffmpeg -hide_banner -loglevel error -y -f concat -safe 0 -i $concatFile -c copy $adaptive
if ($LASTEXITCODE) { throw 'Adaptive segment concatenation failed.' }
$adaptiveWatch.Stop()
$adaptiveEncodeSeconds += $adaptiveWatch.Elapsed.TotalSeconds

function Probe([string]$path) {
    $json = & $Ffprobe -v error -show_entries format=duration,size,bit_rate -of json $path | ConvertFrom-Json
    [pscustomobject]@{ Duration=[double]$json.format.duration; Bytes=[long]$json.format.size; BitrateKbps=[double]$json.format.bit_rate / 1000 }
}
function Quality([string]$encoded, [string]$prefix) {
    $log = & $Ffmpeg -hide_banner -i $encoded -i $reference -lavfi "[0:v][1:v]psnr;[0:v][1:v]ssim" -f null NUL 2>&1
    $psnr = [regex]::Match(($log -join "`n"), 'average:([0-9.]+)').Groups[1].Value
    $ssim = [regex]::Match(($log -join "`n"), 'All:([0-9.]+)').Groups[1].Value
    [pscustomobject]@{ Psnr=[double]$psnr; Ssim=[double]$ssim }
}
$baseProbe=Probe $baseline; $adaptiveProbe=Probe $adaptive
$baseQuality=Quality $baseline 'baseline'; $adaptiveQuality=Quality $adaptive 'adaptive'
$engineManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../upstream.json') -Raw | ConvertFrom-Json
$controllerHash = (Get-FileHash -LiteralPath $header -Algorithm SHA256).Hash
$report = [ordered]@{
    schemaVersion=1; engineVersion=$engineManifest.engineVersion; controllerSha256=$controllerHash
    workload="motion,static,high-motion,static-bars"; width=$Width; height=$Height; fps=$Fps
    secondsPerPhase=$SecondsPerPhase; encoder=$Encoder; ceilingKbps=$CeilingKbps; adaptiveFloorKbps=$floorKbps
    baseline=[ordered]@{ bytes=$baseProbe.Bytes; bitrateKbps=[Math]::Round($baseProbe.BitrateKbps,2); psnr=[Math]::Round($baseQuality.Psnr,4); ssim=[Math]::Round($baseQuality.Ssim,6); encodeSeconds=[Math]::Round($baselineSeconds,3) }
    adaptive=[ordered]@{ bytes=$adaptiveProbe.Bytes; bitrateKbps=[Math]::Round($adaptiveProbe.BitrateKbps,2); psnr=[Math]::Round($adaptiveQuality.Psnr,4); ssim=[Math]::Round($adaptiveQuality.Ssim,6); encodeSeconds=[Math]::Round($adaptiveEncodeSeconds,3) }
    delta=[ordered]@{ bitratePercent=[Math]::Round(100*($adaptiveProbe.BitrateKbps-$baseProbe.BitrateKbps)/$baseProbe.BitrateKbps,2); psnrDb=[Math]::Round($adaptiveQuality.Psnr-$baseQuality.Psnr,4); ssim=[Math]::Round($adaptiveQuality.Ssim-$baseQuality.Ssim,6) }
    notes=@('Codec A/B uses the production demand floor on labeled static phases to measure bitrate-quality sensitivity; it does not include detection delay.','Policy timing and target integral are measured separately by the exact production controller.','The production target may be above this floor when measured output demand is higher.','VMAF is omitted because this FFmpeg build has no libvmaf filter.')
}
$reportPath = Join-Path $outputPath 'benchmark-report.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output "Benchmark report: $reportPath"
$report | ConvertTo-Json -Depth 6
