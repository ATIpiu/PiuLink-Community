# Windows capture and encoding on the Android tablet

The Windows x64 candidate at `c88a8d1` was built from the public `windows-0.2.144` tag. The physical M367FC tablet used the existing signed Android 0.3.265 (285) client. A new pairing in a temporary tablet user connected to the isolated PiuLink Host. The installed daily Host was checked separately with its existing pairing after the tests.

## What moved

The Windows engine calls Rust for DXGI output duplication, deciding how to reuse the last GPU frame when only the cursor changes, creating the D3D11 encoder input texture, enumerating supported NVENC codecs, and releasing acquired encoder resources. If bitstream allocation fails after event registration succeeds, the initializer now unregisters that event. The Rust suite passed 41 tests.

Native code still performs GPU cursor compositing, some texture copy and registration operations, and parts of encoder configuration. The software path still uses FFmpeg and libx264. The Windows changes did not modify the Android decoder. AMD and Intel hardware encoding were not exercised on this machine.

## Physical streaming check

Each session switched a Windows test window through red, green and blue. All three changes were observed at matching positions on the tablet in each session. The encoder was identified in the Host log after the client connected. These short checks verify picture delivery. The Android presentation timestamp counts do not measure end-to-end latency or sustained frame rate.

| Windows capture | Encoder on the host | Tablet presentation timestamps | Observation window |
| --- | --- | ---: | ---: |
| DXGI | NVENC AV1 | 284 | 6.141 s |
| WGC | NVENC AV1 | 263 | 6.066 s |
| DXGI | NVENC HEVC | 262 | 6.100 s |
| DXGI | NVENC H.264 | 238 | 6.266 s |
| DXGI | libx264 | 217 | 6.225 s |

## Windows x64 first-install download

| Build | Compact installer bytes |
| --- | ---: |
| Published 0.2.144 | 95,165,498 |
| 0.2.144 rebuilt with the candidate's packaging toolchain | 95,143,818 |
| Candidate tested above | 95,076,659 |

The candidate saves 67,159 bytes against the rebuilt control and 88,839 bytes against the public download. Its inner installer is 119,533,769 bytes, up from 119,513,339 in the public build. Offline reconstruction reproduced the exact candidate inner installer SHA-256. The installed-file comparison covered 1,074 Host paths and 977 Client paths. Only the engine EXE, Rust DLL and engine manifest changed. The engine EXE and DLL in the bundle matched the physical test binaries.

| Candidate binary | SHA-256 |
| --- | --- |
| PiuLink.Engine.exe | `70b3b3c517da49fa2fa78c48c1136042ed296cc90aad9d636c48552caa635b99` |
| piulink_nvenc_rate.dll | `228054e8f51aa75b569548787838073c2b4b54168eabfe8b111f43e471d96d32` |

## Windows engine download-size experiment

The host's default encoder selection disables CPU AV1 behind `ENABLE_BROKEN_AV1_ENCODER`. The pinned FFmpeg SDK still registers libsvtav1 and pulls SVT-AV1 into the executable. The control link map attributed 7,277,744 bytes of linked sections to that library, 6,568,376 to x265, and 2,276,664 to x264.

The experiment rebuilt the pinned FFmpeg 8.1.1 codec registration unit with the SDK's configuration and the same codec order, dropping only libsvtav1. It then linked the same host objects without the SVT archive. For a dependency build, configure the pinned build-deps FFmpeg project with `-DBUILD_FFMPEG_SVT_AV1=OFF` and pass its installed SDK directory to `apps/stream-engine/Build-Engine.ps1 -FfmpegDirectory <sdk>`. Omitting that argument restores the default SDK. The SDK selection and cache reset were checked separately from the experimental relink.

| Measurement in bytes | Capture candidate | Without software AV1 |
| --- | ---: | ---: |
| Windows engine EXE | 36,615,680 | 29,311,488 |
| Windows x64 integrated first-install download | 95,076,659 | 92,628,687 |

The two bundles had the same 2,051 installed paths. Only the engine executable and its manifest changed. Offline reconstruction reproduced the lean candidate bundle's SHA-256. A Windows client streamed from the lean engine over loopback using NVENC AV1, libx264 and libx265. The host reported the selected encoder after connection and each client showed the three desktop test colors. The software HEVC session logged an unmet IDR request in both the control and candidate builds. This experiment did not repeat the tablet test or measure long-session performance.

## Display recovery on reconnect

An authenticated Windows client streamed from an isolated host and released its display lease. The QA setup simulated an unavailable recovery worker in its own mutex namespace. On the old host, eight consecutive selection requests returned HTTP 409 over 4.329 seconds and the journal still said `Aborted`. With the change, the first request returned HTTP 200 after 3.000 seconds and the journal recorded the new request as `Ready`. These timings cover display preparation after disconnect. A retained application or live stream still prevents eager recovery.
