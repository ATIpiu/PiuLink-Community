[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallerPath, [switch]$LayoutOnly, [switch]$UseThisComputersMicrophone, [switch]$PublicPreview, [switch]$RequireCli)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $InstallerPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($path, 0)
$query = $database.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'")
$query.Execute()
$packageVersion = [version]$query.Fetch().StringData(1)
$nativeMicrophone = $packageVersion -ge [version]'0.2.101' -and !$UseThisComputersMicrophone -and !$PublicPreview
$query.Close()
$view = $database.OpenView("SELECT `Name` FROM `_Tables` WHERE `Name`='RemoveRegistry'")
$view.Execute()
if ($view.Fetch()) { throw 'Installer must not contain a RemoveRegistry table.' }
$view.Close()
$view = $database.OpenView('SELECT `Key`, `Name` FROM `Registry`')
$view.Execute()
while ($record = $view.Fetch()) {
    $registryKey = $record.StringData(1)
    $registryName = $record.StringData(2)
    if ($registryName -in @('-', '*') -or
        ($registryKey -eq 'Software\ATIPiu Labs\PiuLink Host' -and
         $registryName -in @(
            'ActivationInstallationId',
            'FileTransferDevices',
            'FileTransferPeers',
            'AccountClientId'))) {
        throw "Installer attempts to manage persistent PiuLink identity data: $registryKey\$registryName"
    }
}
$view.Close()
$view = $database.OpenView('SELECT `FileName` FROM `File`')
$view.Execute()
$files = @()
while ($record = $view.Fetch()) { $files += ($record.StringData(1) -split '\|')[-1] }
$view.Close()
$requiredFiles = @('sunshine.exe', 'sunshinesvc.exe', 'piulink-engine.json', 'PiuLink.ShareTarget.exe', 'PiuLink.Share.msix', 'Register-ShareTarget.ps1')
if ($RequireCli) { $requiredFiles += @('piulink-host.exe', 'piulink-host.dll', 'piulink-host.deps.json', 'piulink-host.runtimeconfig.json') }
if (($UseThisComputersMicrophone -or $PublicPreview) -and ($LayoutOnly -or ($files -match '\.(sys|cat|inf)$'))) {
    throw 'A driver-free installer must not contain drivers or use the layout-only mode.'
}
if ($nativeMicrophone) { $requiredFiles += @('PiuLinkMic.sys','PiuLinkMic.inf','PiuLinkMic.cat') }
foreach ($required in $requiredFiles) {
    if (@($files | Where-Object { $_ -eq $required }).Count -ne 1) { throw "Expected one embedded $required" }
}
if (@($files | Where-Object { $_ -match 'Sunshine-Windows.*\.msi|sunshine\.(conf|log)|sunshine_state\.json|^credentials$' }).Count) {
    throw 'Installer contains a nested installer or runtime configuration.'
}
$view = $database.OpenView('SELECT `Name`, `DisplayName` FROM `ServiceInstall`')
$view.Execute()
$found = $false
while ($record = $view.Fetch()) {
    if ($record.StringData(1) -eq 'SunshineService' -and $record.StringData(2) -eq 'PiuLink Streaming Engine') { $found = $true }
}
$view.Close()
if (!$found) { throw 'Integrated engine service registration is missing.' }
$view = $database.OpenView('SELECT `Condition`, `Description` FROM `LaunchCondition`')
$view.Execute()
$found = $false
$localGuard = $false
$expectedGuard = 'Installed OR (WIX_UPGRADE_DETECTED AND ComputerName ~= "' + [Environment]::MachineName + '" AND PIULINK_LOCAL_MIC_SERVICE ~= "SteamStreamingMicrophone")'
while ($record = $view.Fetch()) {
    if ($PublicPreview -and $record.StringData(1) -match 'ComputerName|PIULINK_LOCAL_MIC_SERVICE|^Installed$') {
        throw 'A public preview must allow a fresh install on another computer.'
    }
    if ($record.StringData(1) -eq $expectedGuard) { $localGuard = $true }
    if ($record.StringData(1) -match 'EXISTING_STREAM_SERVICE') { $found = $true }
    if ($record.StringData(2) -like 'Packaging validation only*' -and !$LayoutOnly) {
        throw 'This MSI is for layout verification only and is blocked from installation.'
    }
}
$view.Close()
if (!$found) { throw 'Existing-service migration guard is missing.' }
if ($localGuard -ne $UseThisComputersMicrophone.IsPresent) { throw 'The machine-bound local microphone upgrade guard does not match the requested installer mode.' }
if ($UseThisComputersMicrophone) {
    $view = $database.OpenView("SELECT Signature_ FROM AppSearch WHERE Property='PIULINK_LOCAL_MIC_SERVICE'")
    $view.Execute()
    $signature = $view.Fetch().StringData(1)
    $view.Close()
    $view = $database.OpenView("SELECT * FROM RegLocator WHERE Signature_='$signature'")
    $view.Execute()
    $locator = $view.Fetch()
    if (!$locator -or $locator.IntegerData(2) -ne 2 -or $locator.StringData(3) -ne 'SYSTEM\CurrentControlSet\Enum\ROOT\PIULINKVIRTUALMICROPHONE\0000' -or $locator.StringData(4) -ne 'Service') {
        throw 'Local microphone binding lookup is missing.'
    }
    $view.Close()
    $view = $database.OpenView('SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')
    $view.Execute()
    $sequence = @{}
    while ($record = $view.Fetch()) { $sequence[$record.StringData(1)] = $record.IntegerData(2) }
    $view.Close()
    if ($sequence['RemoveExistingProducts'] -le $sequence['InstallInitialize'] -or $sequence['RemoveExistingProducts'] -ge $sequence['InstallFiles']) {
        throw 'Local upgrade must fully remove the previous engine before installing the new files, within the rollback transaction.'
    }
    Write-Output 'PASS local upgrade guard, existing microphone binding and transactional engine replacement.'
}
$view = $database.OpenView('SELECT `Action`, `Type`, `Target` FROM `CustomAction`')
$view.Execute()
$actions = @()
while ($record = $view.Fetch()) {
    $action = $record.StringData(1)
    $actions += $action
    if ($packageVersion -ge [version]'0.2.102') {
        if ($action -eq 'RegisterPiuLinkShareTarget' -and ($record.IntegerData(2) -band 64)) {
            throw 'Share registration failures must be reported to Windows Installer.'
        }
        if ($action -in 'InstallBundledMicrophone','RollbackBundledMicrophone','CommitBundledMicrophone' -and
            !$record.StringData(3).Contains('[ProductCode]')) { throw "Missing microphone transaction identity: $action" }
    }
}
$view.Close()
$micActions = @('InstallBundledMicrophone','RollbackBundledMicrophone','CommitBundledMicrophone','RemoveOwnedMicrophoneDevice')
if (($UseThisComputersMicrophone -or $PublicPreview) -and ($actions | Where-Object { $_ -in $micActions })) { throw 'This installer must leave microphone devices alone.' }
if ($PublicPreview) {
    $view = $database.OpenView("SELECT `Condition` FROM `InstallExecuteSequence` WHERE `Action`='RegisterPiuLinkShareTarget'")
    $view.Execute()
    if ($view.Fetch().StringData(1) -ne 'NOT (REMOVE~="ALL") AND PIULINK_REGISTER_SHARE=1') {
        throw 'Unexpected ShareTarget registration condition for the public preview.'
    }
    $view.Close()
}
$requiredActions = @('RegisterPiuLinkShareTarget', 'UnregisterPiuLinkShareTarget')
if ($nativeMicrophone) { $requiredActions += @('InstallBundledMicrophone','RollbackBundledMicrophone','CommitBundledMicrophone') }
foreach ($requiredAction in $requiredActions) {
    if ($requiredAction -notin $actions) { throw "Share target custom action is missing: $requiredAction" }
}
if ($nativeMicrophone) {
    $view = $database.OpenView('SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')
    $view.Execute()
    $order = @{}
    while ($record = $view.Fetch()) { $order[$record.StringData(1)] = $record.IntegerData(2) }
    $view.Close()
    $previous = 0
    foreach ($action in 'InstallFiles','RollbackBundledMicrophone','InstallBundledMicrophone','CommitBundledMicrophone','InstallFinalize') {
        if ($order[$action] -le $previous) { throw "Unexpected MSI microphone action order: $action" }
        $previous = $order[$action]
    }
    Write-Output 'PASS bundled PiuLink microphone, install and rollback sequence.'
}
Write-Output 'PASS embedded engine, share target, service registration, migration guard, persistent identity, no nested installer or runtime config'
