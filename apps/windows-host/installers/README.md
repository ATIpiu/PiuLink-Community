# Installer and update safety gate

## Bundled Sunshine policy

The Windows release may convey the pinned Sunshine MSI as a separate GPLv3 component. Run `Acquire-Sunshine.ps1` to acquire the reviewed artifact into the ignored `cache` directory. The script rejects a changed hash, invalid Authenticode signature, signer-subject change or signer-certificate change.

The PiuLink UI must obtain explicit consent before elevation. It may execute only the exact staged MSI through `msiexec`; it must not accept a URL, arbitrary path or shell command from the UI. PiuLink and Sunshine remain separate executables and communicate through the loopback REST API.

Before any public installer ships, run `Export-Sunshine-Source.ps1` and host its complete corresponding-source archive alongside the binary release. The script locks the exact commit, initializes required submodules and reports the archive SHA-256. Shipping only an upstream link is not the final compliance state.

## Safety requirements

Before a bundled component or PiuLink update can be downloaded and executed, the release pipeline must publish a reviewed manifest containing:

- immutable version and HTTPS artifact URL;
- exact SHA-256;
- expected Authenticode signer subject;
- upstream source/tag and license evidence;
- supported unattended arguments and rollback behavior.

The Host must download to a private temporary directory, verify hash and Authenticode status, show the publisher/version to the user, request explicit consent, elevate only the installer process and re-probe health afterward. A web page or mutable “latest” URL is never sufficient authorization to execute a file.

## Build the internal PiuLink MSI

`PiuLink.Host.Setup` uses WiX Toolset 6 to publish a self-contained Windows x64 Host and package it per-machine. The MSI includes the locally acquired, verified Sunshine payload for the in-app consent flow.

```powershell
dotnet build .\installers\PiuLink.Host.Setup\PiuLink.Host.Setup.wixproj -c Release
```

The resulting unsigned internal installer is under `installers\PiuLink.Host.Setup\bin\x64\Release`. Public distribution remains blocked until PiuLink's MSI and every PE file meet Microsoft Store signing requirements.
