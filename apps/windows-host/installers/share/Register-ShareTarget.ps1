param(
    [Parameter(Mandatory)]
    [ValidateSet('Register', 'Unregister')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
$packageName = 'ATIPiuLabs.PiuLink.Share'

$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($Action -eq 'Unregister' -and $installed) {
    $installed | Remove-AppxPackage
}

if ($Action -eq 'Register') {
    $root = [IO.Path]::GetFullPath($PSScriptRoot)
    $packagePath = Join-Path $root 'PiuLink.Share.msix'
    $executablePath = Join-Path $root 'ShareTarget\PiuLink.ShareTarget.exe'
    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw "PiuLink share identity package is missing: $packagePath"
    }
    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "PiuLink share target is missing: $executablePath"
    }
    Add-AppxPackage -Path $packagePath -ExternalLocation $root -ForceUpdateFromAnyVersion
    $registered = Get-AppxPackage -Name $packageName -ErrorAction Stop
    if (!$registered -or $registered.Status -ne 'Ok') { throw 'Windows did not register the PiuLink share target for this user.' }
}
