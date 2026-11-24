[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$credentialStore = [IO.File]::ReadAllText((Join-Path $repoRoot 'apps/windows-host/src/PiuLink.Host/Services/WindowsCredentialStore.cs'))
$managedIdentity = [IO.File]::ReadAllText((Join-Path $repoRoot 'apps/windows-host/src/PiuLink.Host/Services/SunshineManagedIdentity.cs'))
$hostProject = [IO.File]::ReadAllText((Join-Path $repoRoot 'apps/windows-host/src/PiuLink.Host/PiuLink.Host.csproj'))
$packageSource = [IO.File]::ReadAllText((Join-Path $repoRoot 'apps/windows-host/installers/PiuLink.Host.Setup/Package.wxs'))

if ($credentialStore -match 'CredDelete|\bvoid\s+Delete\s*\(') {
    throw 'PiuLink application code exposes a credential deletion path.'
}
if ($credentialStore -notmatch 'CredentialPersistLocalMachine\s*=\s*2') {
    throw 'PiuLink credentials are not configured for Windows local-machine persistence.'
}
if ($managedIdentity -match 'forceNewCredential|TryRead\(\)\s*\?\?\s*CreateCredential\(\)\s*:') {
    throw 'Sunshine repair can rotate an existing Windows credential.'
}
if ($managedIdentity -notmatch 'var credential = TryRead\(\) \?\? CreateCredential\(\);') {
    throw 'Sunshine repair does not reuse the durable Windows credential.'
}
if ($hostProject -notmatch 'Exclude="\$\(PiuLinkEngineDirectory\)\\config\\\*\*;') {
    throw 'The installer publish step could overwrite Sunshine runtime credentials/configuration.'
}
if ($packageSource -match '<RemoveRegistry|Name="[-*]"') {
    throw 'The MSI source contains broad registry removal instructions.'
}

Write-Output 'PASS reinstall preserves Sunshine login, pairings and engine configuration'
