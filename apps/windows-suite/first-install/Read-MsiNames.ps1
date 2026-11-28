param([Parameter(Mandatory)][string]$Msi)
$ErrorActionPreference = 'Stop'
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($Msi, 0)
$view = $db.OpenView('SELECT `File`, `FileName` FROM `File`')
$null = $view.Execute()
$names = @{}
while ($row = $view.Fetch()) {
    $names[$row.StringData(1)] = $row.StringData(2).Split('|')[-1]
}
$null = $view.Close()
$names | ConvertTo-Json -Compress
