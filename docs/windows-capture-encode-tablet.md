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
