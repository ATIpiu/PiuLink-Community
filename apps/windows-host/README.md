# PiuLink Host for Windows

Windows Host that reports whether the machine is ready for remote streaming and provides a native
Sunshine control surface. Host runs without a purchase or activation check.
Reviewed builds carry a pinned Sunshine MSI as a separate GPLv3 component; Tailscale remains an
official-download guided install.

## Implemented

- Windows version and CPU architecture detection.
- Display-adapter detection with NVIDIA/AMD/Intel classification.
- Tailscale executable, backend state, device name and overlay IP detection.
- In-app Tailscale installation guide using the official download source.
- Direct `tailscale.exe up` sign-in flow after installation; PiuLink never handles the account password.
- Native management of this PC's supported Tailscale preferences: tailnet DNS, subnet routes,
  automatic updates, SSH, device web client, incoming-connection shielding, connect/disconnect and logout.
  Identity sign-in and tailnet-wide ACL administration remain on Tailscale's official secure pages.
- Sunshine executable and Windows service-state detection.
- Sunshine automatic-start, local Web endpoint and Windows Firewall rule checks.
- Native Sunshine connection through the loopback REST API; no Sunshine browser window is opened.
- Streamable-application and paired-client counts inside PiuLink Host.
- Current Sunshine application and active-stream detection, with a guarded one-click cleanup for a retained application after all clients disconnect.
- Session Guardian observes a retained app for 12 seconds, safely releases it when no client is connected, and escalates to a verified Sunshine restart only if the normal close path fails.
- Physical/VDD display discovery from Sunshine's local log, including primary state, resolution and refresh rate.
- Client-controlled streaming target: a named physical screen or “Gather all windows” on an installed virtual display.
- The virtual target keeps one of the VDD's advertised modes. The client scales the stream instead of asking Windows for an unsupported device-native phone resolution.
- Hidden-window safety automatically returns stranded app windows to the primary display after a VDD disappears, with a manual recovery action in the Displays page.
- Optional `ensure_only_display` mode that temporarily uses only the selected display during a stream and lets Sunshine restore the previous layout afterward.
- Saving an exclusive virtual target disconnects its desktop path while retaining the enabled driver. Sunshine activates it on connection and restores the physical-only idle layout after the last client disconnects. Preparing this mode requires an active physical display; PiuLink refuses to disconnect the last screen.
- With automatic window recovery enabled and Host running in the tray, physical-desktop window placements are sampled before streaming and restored after the physical layout returns. Closed windows and changed monitor layouts are skipped. Snapshots are in memory and do not survive a Host restart; application-specific DPI behavior still requires testing on the actual client.
- Safe full-configuration preservation and local Sunshine restart, including restart-disconnect verification; display changes are disabled during an active stream.
- Complete Sunshine configuration editor inside PiuLink. Response-only metadata is stripped, invalid JSON
  is rejected before saving, unknown or future fields are preserved, and saving is locked during a stream.
- Moonlight pairing PIN submission inside PiuLink Host.
- PiuLink generates one device-only Sunshine control credential and protects it in Windows Credential Manager; users do not manage a second Sunshine identity.
- The Sunshine control credential uses Windows local-machine persistence and is not deleted or rotated by Host upgrade, uninstall, reinstall, or repair.
- Explicit-consent Sunshine installation from an offline payload pinned by version, SHA-256, Authenticode signer subject and certificate thumbprint.
- Bundled GPLv3 license and Sunshine source/notice links; public distribution remains gated on publishing complete corresponding source for the exact binary.
- Combined Host Ready result, copyable connection summary, local log and diagnostic export.
- Safe process execution with argument lists, timeouts and captured output.
- Console diagnostic mode for automation and support.
- SHA-256/manifest foundation for future signed installers and updates.
- Canonical black-on-white PiuLink icon embedded in the EXE, window, tray and installer shortcut, with prepared Microsoft Store package/listing sizes.
- A dedicated Wake-on-LAN page that lists active Ethernet/Wi-Fi MAC addresses, checks Windows wake-armed devices, copies the selected MAC, and links directly to Device Manager and Power Options without silently changing driver or firmware settings.
- Closing or minimizing the Host window keeps it running in the notification area. Only the explicit
  `Exit PiuLink` tray command stops the application.
- One Host instance is allowed machine-wide, shared across installation paths.
  Launching again signals the existing instance to show and activate its window, preserving maximized state.
  If another Windows user/session owns the instance and cannot
  be activated from this desktop, the launcher reports that instead of starting a second Host.
- No PiuLink account, activation service dependency, online-node directory, hosted punching service or media relay.
- LAN, the user's own Tailscale network and manual addresses are the supported connection routes.

## Build

To use the automatic virtual desktop, choose **Gather all windows** in the client before connecting.
The VDD remains installed and detectable but does not occupy desktop space while idle; do not disable
its PnP driver between sessions. Host applies the client's stable device ID and restores the physical
desktop after disconnect.

Display regression checks (includes read-only Windows display enumeration, never switches screens):

```powershell
dotnet run --project apps/windows-host/tests/PiuLink.Host.Display.Tests/PiuLink.Host.Display.Tests.csproj
dotnet run --project apps/windows-host/tests/PiuLink.Host.Instance.Tests/PiuLink.Host.Instance.Tests.csproj
./apps/windows-host/tests/Test-CredentialPersistence.ps1
```

Hardware acceptance: start with physical desktop only; connect a client and verify only the VDD is active;
disconnect the last client and verify physical monitors, window sizes and maximized states return.
Repeat with a different client resolution, reconnect during the grace period, and quit the client abruptly.
These interactive streaming checks are separate from the automated tests.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\apps\windows-host\PiuLink.Host.sln -c Release
```

The MSI includes `piulink-host.exe` for diagnostics, pairing, stream management and desktop
recovery. See the [Windows CLI guide](../../docs/CLI.md). Run diagnostics from source with:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\apps\windows-host\src\PiuLink.Host.Cli\PiuLink.Host.Cli.csproj -c Release -- doctor --json
```

Build the Microsoft Store MSIX after reserving the app identity in Partner Center:

```powershell
.\apps\windows-host\installers\store\Build-StoreMsix.ps1 `
  -IdentityName 'PartnerCenter.Identity.Name' `
  -Publisher 'CN=PARTNER-CENTER-PUBLISHER-ID'
```

Build the open-source MSI. Omit `-Standalone` for the compact MSI (requires .NET 8 Desktop Runtime
x64); add `-Standalone` for a larger no-prerequisite MSI:

```powershell
.\apps\windows-host\installers\Build-OpenSourceHost.ps1 `
  -ShareSigningCertificateThumbprint '<trusted-code-signing-certificate-thumbprint>'
```

The build copies the signing certificate subject into the sparse package publisher, then embeds the
signed MSIX identity, the ShareTarget bridge, and its per-user registration
script into the Host MSI. The local `CN=ATIPiu Labs` test certificate is suitable only for development
machines where it is explicitly trusted; production packages require the release certificate.

Firewall, driver installation/removal and general service mutations remain held behind a separate privileged-operation design. The current Sunshine installer path can execute only the reviewed offline MSI after verification and explicit consent.
PiuLink 0.2.7 safely refreshes Sunshine after a virtual display is enabled or disabled. Active streams are protected and the refresh waits until disconnect.
It lets display changes made during an active stream wait safely for the next connection and keeps that
pending choice across Host restarts. Display selection persists automatically, a newly selected
target defaults to single-display streaming, and the original topology returns after disconnect. It also
includes persistent close-to-tray behavior, native Tailscale device settings, a complete Sunshine
configuration editor, the persistent in-app language selector, Session Guardian,
client-matched smart display routing and the Virtual display navigation page. For an already installed
MikeTheTech VDD, PiuLink can explicitly enable or disable only the fixed `Root\MttVDD` device after Windows
shows a UAC prompt. It does not bundle or silently install the driver.
