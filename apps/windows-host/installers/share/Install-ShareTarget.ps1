param([string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
if (!$PayloadDirectory) { throw 'Supply the signed share payload directory.' }
$target = Join-Path $env:ProgramFiles 'ATIPiu Labs\PiuLink Host'
New-Item -ItemType Directory -Force $target | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $target 'ShareTarget\PiuLink.ShareTarget.exe'))) {
    throw 'Install the ShareTarget payload alongside PiuLink Host first (requires administrator rights).'
}
Add-AppxPackage -Path (Join-Path ([IO.Path]::GetFullPath($PayloadDirectory)) 'PiuLink.Share.msix') -ExternalLocation $target
Get-AppxPackage -Name ATIPiuLabs.PiuLink.Share | Select-Object Name,Version,Status
