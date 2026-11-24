[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../../artifacts/benchmarks/codec-matrix/piulink.6-720p60-20m'),
    [int]$Width = 1280,
    [int]$Height = 720,
    [int]$Fps = 60,
    [int]$SecondsPerPhase = 5,
    [int]$CeilingKbps = 20000
)

$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$runner = Join-Path $PSScriptRoot 'Run-ContentAdaptiveBenchmark.ps1'
$encoders = @('h264_nvenc', 'hevc_nvenc', 'av1_nvenc')
$rows = foreach ($encoder in $encoders) {
    $codecOutput = Join-Path $outputPath $encoder
    & $runner -OutputDirectory $codecOutput -Width $Width -Height $Height -Fps $Fps `
        -SecondsPerPhase $SecondsPerPhase -CeilingKbps $CeilingKbps -Encoder $encoder | Out-Host
    if ($LASTEXITCODE) { throw "Benchmark failed for $encoder" }
    $report = Get-Content -LiteralPath (Join-Path $codecOutput 'benchmark-report.json') -Raw | ConvertFrom-Json
    [pscustomobject]@{
        Encoder = $encoder
        BaselineKbps = $report.baseline.bitrateKbps
        AdaptiveKbps = $report.adaptive.bitrateKbps
        BitrateDeltaPercent = $report.delta.bitratePercent
        BaselinePsnr = $report.baseline.psnr
        AdaptivePsnr = $report.adaptive.psnr
        PsnrDeltaDb = $report.delta.psnrDb
        BaselineSsim = $report.baseline.ssim
        AdaptiveSsim = $report.adaptive.ssim
        SsimDelta = $report.delta.ssim
        BaselineEncodeSeconds = $report.baseline.encodeSeconds
        AdaptiveEncodeSeconds = $report.adaptive.encodeSeconds
    }
}

$csv = Join-Path $outputPath 'codec-matrix.csv'
$json = Join-Path $outputPath 'codec-matrix.json'
$rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding utf8
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $json -Encoding utf8
$rows | Format-Table -AutoSize
Write-Output "Codec matrix JSON: $json"
Write-Output "Codec matrix CSV: $csv"
