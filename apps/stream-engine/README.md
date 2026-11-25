# PiuLink streaming engine

PiuLink is one installed product. The streaming engine remains a separate background
executable based on the pinned Sunshine source. PiuLink owns configuration, installation,
startup, upgrade and rollback; the engine retains its upstream source identity and license.

## Source and build

`upstream.json` pins the exact commit. `patches/` contains only PiuLink's engine changes.
Source checkouts, toolchains and build output remain under ignored `work/` and `artifacts/`.
Do not copy development binaries over an installed Sunshine service.

After installing the Windows dependencies from the pinned upstream `docs/building.md`,
run `Build-Engine.ps1 -SourceDirectory <checkout> -MsysRoot <msys64>`.
The script verifies the checkout, applies the pinned patch and builds/stages a portable engine.
For a development integrated MSI, pass that staged folder to `Build-IntegratedHost.ps1`
with `-Development`. Release builds reject
an engine whose manifest still has `releaseReady: false`.

`Export-Source.ps1` exports the patched source and pinned submodules without build output.
To rebuild from that exported tree, install the upstream Windows build prerequisites,
set `BRANCH=piulink`, `BUILD_VERSION=2026.516.143833`, and `COMMIT` to the pinned commit,
then run `cmake -B build-piulink -G Ninja -S . -DBUILD_DOCS=OFF -DBUILD_TESTS=OFF
-DSUNSHINE_ENABLE_TRAY=OFF -DSUNSHINE_PUBLISHER_NAME=PiuLink`, followed by
`cmake --build build-piulink --parallel 8`. The source archive already includes the patch.

The extension uses the existing encrypted control connection. The server advertises
`piulink_control=1`; clients without this capability keep upstream behavior. Requests use
type `0x55A0` with two little-endian uint32 fields (operation, value); acknowledgements use
`0x55A1` with the same layout. Operations: 0 capabilities, 1 bitrate in Kbps, 2 cursor mode,
3 content-adaptive encoding. Capability bits are bitrate `1`, cursor `2`, and content adaptation
`4`.
An encoder rejection is not success. Cursor mode must be acknowledged before local rendering.

Content-adaptive encoding is currently implemented for the direct NVENC path. It observes
encoded frame cost without reading pixels back from the GPU. After sustained static desktop
content it lowers the active target in bounded steps, down to 40% of the negotiated ceiling;
motion restores quality quickly. Client bitrate changes replace that ceiling. Disabling the
feature restores the full client target immediately. HEVC/H.264/AV1 framing remains unchanged.
Run `Test-ContentAdaptiveRate.ps1` against a patched checkout for the deterministic policy test.

## Integration target

- One PiuLink installer and UI; no separate Sunshine download or setup page.
- Engine binaries under `PiuLink Host/engine/<version>/`, writable settings under ProgramData.
- Detect an existing official Sunshine installation; migrate only through an explicit flow,
  preserve its configuration and never start both services on the same ports.
- Stage and validate an engine before activating it. Upgrade only after streaming ends.
- Ship license notices and the complete corresponding modified source with releases.

## Development status

Not release-ready. Protocol and NVENC reconfiguration changes are under development;
local cursor synchronization, integrated service lifecycle, rollback and end-to-end
validation remain required. A development MSI can now package the engine directly and
register its service and streaming firewall rules. It blocks a conflicting existing Sunshine
service pending a migration flow. Existing production packaging still carries official Sunshine.

## Public Windows Host preview

Build the staged engine with PowerShell 7 and `Build-Engine.ps1 -PublicPreview` using the pinned MSYS2 UCRT64 dependencies. Pass the staged engine directory and the ShareTarget signer to `Build-IntegratedHost.ps1 -Development -PublicPreview`. This profile strips engine debug information, maps compiler input paths, includes the signed ShareTarget and skips installing or removing microphone drivers. The regular driver-bundled and machine-specific profiles retain their own requirements.

Install the Host MSI on the Windows x64 PC to control. A first install on a machine already running a standalone Sunshine service requires migration. Registering the Windows ShareTarget is optional in this preview. On a system that already trusts its signing certificate, run the installed `Register-ShareTarget.ps1 -Action Register` in the intended user's session or pass `PIULINK_REGISTER_SHARE=1` to Windows Installer. The main Host UI provides its own file-transfer entry point.
