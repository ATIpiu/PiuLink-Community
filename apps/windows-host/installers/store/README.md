# Microsoft Store package

PiuLink is submitted as a paid x64 MSIX. Microsoft Store performs acquisition, licensing, updates,
refunds and final package signing. The listing has no free trial, subscription or add-on.

## Build

Reserve the product in Partner Center and copy the exact package Identity name and Publisher values.
The script uses an installed Windows SDK when available and otherwise restores Microsoft's pinned
Windows SDK BuildTools package:

```powershell
.\Build-StoreMsix.ps1 `
  -IdentityName 'PartnerCenter.Identity.Name' `
  -Publisher 'CN=PARTNER-CENTER-PUBLISHER-ID' `
  -PublisherDisplayName 'ATIPiu Labs' `
  -Version '0.2.7.0'
```

The unsigned `.msix` is for Partner Center upload. It cannot be installed locally until it is signed
with a locally trusted development certificate or returned through a Store flight.

Release builds fail closed unless `StoreContext.GetAppLicenseAsync()` returns an active, non-trial
license. Debug builds bypass Store licensing by default. Set `PIULINK_REQUIRE_STORE_LICENSE=1` only
to exercise the Store path from a packaged Debug build associated with the Partner Center sandbox.

The package contains no PiuLink account client, API origin, punching service configuration or tunnel
sidecar. Network choices are LAN, the user's own Tailscale installation and manual addresses.

A package built with placeholder identity values is only a structure test. Do not submit or publish
it; always rebuild with the exact identity copied from Partner Center.
