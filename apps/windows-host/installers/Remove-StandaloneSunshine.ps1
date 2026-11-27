[CmdletBinding()]
param([switch]$PlanOnly)
$ErrorActionPreference = 'Stop'

function Get-InstalledProducts {
    @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue)
}
function Get-SunshineUninstallPlan {
    param($Products)
    foreach ($product in $Products) {
        # Never select Tailscale or unrelated applications by substring.
        if ($product.DisplayName -ne 'Sunshine') { continue }
        $productCode = [guid]::Empty
        if ([guid]::TryParse($product.PSChildName, [ref]$productCode) -and $product.WindowsInstaller -eq 1) {
            [pscustomobject]@{Kind='MSI'; Target=$productCode.ToString('B'); Arguments='/qn /norestart'}
        } elseif ($product.UninstallString -match '^"([^"\r\n]+\\uninstall\.exe)"\s*$') {
            $target = [IO.Path]::GetFullPath($Matches[1])
            if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Sunshine uninstaller is missing.' }
            [pscustomobject]@{Kind='EXE'; Target=$target; Arguments='/S'}
        } else { throw 'Unrecognized Sunshine installer. Remove Sunshine from Windows Installed apps.' }
    }
}

if ($PlanOnly) { Get-SunshineUninstallPlan (Get-InstalledProducts); return }

# Runs after MSI commit, outside the removed installation directory. Wait for
# the parent MSI to release its transaction; never nest another MSI transaction.
$logRoot = Join-Path $env:ProgramData 'ATIPiu Labs\PiuLink Uninstall'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$log = Join-Path $logRoot 'sunshine-removal.log'
try {
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    do {
        if (!(Get-InstalledProducts | Where-Object DisplayName -eq 'PiuLink Host')) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'PiuLink is still registered; Sunshine was preserved.' }
        Start-Sleep -Seconds 2
    } while ($true)
    foreach ($item in @(Get-SunshineUninstallPlan (Get-InstalledProducts))) {
        do {
            if ($item.Kind -eq 'MSI') {
                $process = Start-Process "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/x', $item.Target, '/qn', '/norestart') -WindowStyle Hidden -PassThru -Wait
            } else {
                $process = Start-Process -FilePath $item.Target -ArgumentList $item.Arguments -WindowStyle Hidden -PassThru -Wait
            }
            if ($process.ExitCode -ne 1618) { break }
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Windows Installer remained busy; Sunshine removal needs retry.' }
            Start-Sleep -Seconds 2
        } while ($true)
        if ($process.ExitCode -notin @(0,1605,1614,3010)) { throw "Sunshine uninstall failed: $($process.ExitCode)" }
    }
    if (Get-InstalledProducts | Where-Object DisplayName -eq 'Sunshine') { throw 'Sunshine remains registered after uninstall.' }
    'Completed. Sunshine removed (or absent); Tailscale preserved.' | Set-Content -LiteralPath $log
} catch { $_.Exception.Message | Set-Content -LiteralPath $log; exit 1 }
