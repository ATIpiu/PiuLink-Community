# PiuLink for Android Privacy Policy

## Publication blockers

Supply the legal publisher/controller, contact email, public HTTPS policy URL and effective date,
then obtain legal review before linking this document from Google Play.

## Summary

- PiuLink has no user account, identity server, device directory, NAT-traversal server, media relay,
  advertising SDK, analytics SDK or third-party crash-reporting SDK.
- PiuLink does not receive a Google password, Google identity token or payment-card information.
- App settings, saved Hosts and pairing material are stored locally and excluded from the app's
  configured cloud-backup/device-transfer categories.
- Stream media and input travel between the Android device and the compatible Host selected by the
  user, not through a PiuLink server.

## Local information

PiuLink may store preferences, streaming/input settings, Host names and addresses, pairing
certificates, client credentials, cached artwork and temporary app data. Users can remove saved Hosts,
clear app storage or uninstall the app.

## Network information

The app exchanges pairing, control, diagnostics, media and input protocol data with the Host selected
by the user. LAN discovery stays on the local network. A connectivity check may contact the
third-party `stun.moonlight-stream.org` service, whose operator can observe the request IP and basic
network metadata.

If the user opens a separately installed Tailscale/VPN app, that provider processes network data
under its own policy. PiuLink does not bundle or operate that network service.

## Accessibility and keyboard capture

The optional AccessibilityService captures system-reserved keyboard shortcuts only while the user is
actively streaming and has enabled it. Captured key presses may be sent to the selected Host as
remote input; PiuLink does not retain or send them to a PiuLink server. Users can revoke the permission
in Android Settings.

## Google Play and payments

The current app contains no in-app payment SDK. Google Play may independently process acquisition,
licensing, diagnostics and account data under Google's terms. PiuLink does not link that identity to
a Microsoft Store identity or operate cross-store purchase restoration.

## Security and contact

No network or storage system is perfectly secure. Users are responsible for their Host, router,
remote-access configuration and third-party network accounts. Contact **[PRIVACY EMAIL]**. Finalize
children's privacy, jurisdiction, transfer and change-notice language before publication.
