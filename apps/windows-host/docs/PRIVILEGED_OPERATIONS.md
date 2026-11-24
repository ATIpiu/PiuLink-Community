# Privileged-operation design

The WPF UI remains `asInvoker`. There are two narrow internal-alpha exceptions after explicit user action:

1. The Sunshine path re-verifies the fixed offline MSI and asks Windows to elevate `msiexec` for that exact path. It accepts no URL, user-selected executable, shell string or installer arguments.
2. Virtual-display lifecycle control verifies the installed device has the exact `Root\MttVDD` hardware ID and an instance ID matching `ROOT\DISPLAY\dddd`, then elevates Windows `pnputil.exe` with only `/enable-device` or `/disable-device` for that fixed instance. It accepts no user-controlled device ID, executable, path or additional arguments. Installation and removal remain outside the UI.

A production broker or signed bootstrapper performs all other allow-listed operations described by a signed request:

1. Install a manifest-pinned, verified MSI/EXE, including moving the current Sunshine exception out of the UI before public release if the final bootstrapper does not install it initially.
2. Start/restart an allow-listed Sunshine service.
3. Add/remove PiuLink-owned firewall rules with stable identifiers.
4. Install/remove a specifically pinned and signed virtual-display driver, and move the current internal-alpha enable/disable exception into this broker before public release.

The broker must reject shell strings, arbitrary executable paths, mutable URLs, unknown service names and wildcard firewall deletion. Every mutation records before/after state and a rollback action. The user sees an elevation prompt immediately before the mutation.
