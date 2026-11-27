# PiuLink content-adaptive benchmark

This benchmark keeps two claims separate:

1. `content_adaptive_policy_benchmark.cpp` runs the exact production controller and measures its
   transition timing, target bitrate integral and reconfiguration count.
2. `Run-ContentAdaptiveBenchmark.ps1` generates one deterministic four-phase video and compares a
   fixed-ceiling encode with the production demand floor on labeled motion/static phases. The p6
   controller derives its real target from recent output bitrate with 25% headroom, a 60%/4 Mbps
   safety floor, a 15% minimum-benefit gate, and NVENC average-QP quality pressure. This codec
   sensitivity A/B intentionally excludes detection delay. It reports file bytes, average bitrate,
   PSNR, SSIM and wall-clock encoding time.

The synthetic codec comparison is an offline controlled benchmark, not proof of end-to-end gaming
quality. Release validation must additionally capture a real PiuLink session and compare dropped
frames, RTT, encode latency and VMAF on identical lossless captures. VMAF is emitted only when the
selected FFmpeg contains `libvmaf`; the currently installed build exposes PSNR and SSIM only.

Do not compare the reported wall-clock times as encoder overhead: the adaptive fixture deliberately
encodes four independent segments while the baseline encodes once. Use them only to spot gross
regressions within runs that use the same fixture structure.

Run from the repository root:

```powershell
./apps/stream-engine/benchmarks/Run-ContentAdaptiveBenchmark.ps1
```

For a fast smoke run:

```powershell
./apps/stream-engine/benchmarks/Run-ContentAdaptiveBenchmark.ps1 `
  -Width 640 -Height 360 -Fps 30 -SecondsPerPhase 2 -CeilingKbps 8000
```

Run the six-group H.264/HEVC/AV1 fixed-versus-adaptive matrix:

```powershell
./apps/stream-engine/benchmarks/Run-CodecMatrix.ps1
```

Results are written under `artifacts/benchmarks/content-adaptive`. Keep the JSON report, transition
CSV, encoder name, GPU driver and engine version together when comparing algorithm revisions.
The runner defaults to `work/piulink-engine-source`, which is the checkout used by `Build-Engine.ps1`;
the controller SHA-256 is embedded in every report to prevent stale worktrees from being compared.
