# PiuLink Windows Client

PiuLink Client is a native Windows viewer/controller for a PiuLink Host running Sunshine. It supports
saved and discovered Hosts, pairing, application launch, embedded interactive streaming, audio,
display selection, codec/bitrate controls, clean disconnect, and abnormal-session recovery.

The MSI and portable distributions include `piulink.exe` for discovery, pairing, streaming,
and automation. See the [Windows CLI guide](../../docs/CLI.md) for usage and examples.

## Architecture

- `PiuLink.Client` (WPF) owns profiles, display preparation, pairing UX and session lifecycle.
- The bundled `PiuLink-Moonlight.exe` is the Qt Moonlight engine for manual PIN pairing and
  authenticated application-list retrieval. WPF displays the PIN and launches the headless Qt
  command; the Host-side PIN confirmation remains manual. Qt therefore retains the client
  identity and pairing certificate it uses for these operations.
- `GameStreamHttpClient` follows the same split as the Android client: it owns the persistent client
  certificate, pins the Host certificate, executes the five-stage GameStream pairing exchange, reads
  `serverinfo`/`applist`, and performs authenticated `launch`/`resume` negotiation.
- Pairing and streaming use the same Qt Moonlight identity. WPF prepares the Host display and starts
  the Qt engine's native stream window, so a successful Qt PIN pairing is immediately usable for
  application launch. `PiuLink.Native.dll` remains packaged for the embedded-stream compatibility
  path while the Qt engine is the active end-to-end engine.

The source remains GPL-3.0-only because it incorporates Moonlight components. Corresponding license
and notice files are installed beside the native library.

## Reproducible build

From the repository root on the pinned Windows/MSVC machine:

```powershell
.\apps\windows-client\native-engine\Build-NativeEngine.ps1
.\apps\windows-client\engine\Build-Engine.ps1
dotnet build .\apps\windows-client\PiuLink.Client.sln -c Release
dotnet build .\apps\windows-client\installers\PiuLink.Client.Setup -c Release
```

The native streaming payload is written to `artifacts/windows-engine/PiuLink-Native-0.1.0-x64` and
the Qt Moonlight payload is written to `artifacts/windows-engine/PiuLink-Moonlight-<version>-x64`.
Both are staged into the client package; the MSI is written under
`apps/windows-client/installers/PiuLink.Client.Setup/bin/x64/Release`.

## Physical-host smoke test

The E2E utility uses the same protocol classes as the installed client. The pairing command starts
the protocol request before asking the Host companion to approve the same PIN.

```powershell
dotnet run --project .\apps\windows-client\tests\PiuLink.Native.E2E -c Release -- pair HOST
dotnet run --project .\apps\windows-client\tests\PiuLink.Native.E2E -c Release -- apps HOST
dotnet run --project .\apps\windows-client\tests\PiuLink.Native.E2E -c Release -- dllrun HOST Desktop
```

The diagnostic utility remains available for protocol-level testing. The shipped WPF Client uses the
same protocol implementation through the in-process native DLL and renders the stream in its own main
window.
