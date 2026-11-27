$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Remove-StandaloneSunshine.ps1') -PlanOnly | Out-Null
$code='{11111111-1111-1111-1111-111111111111}'
$products=@(
    [pscustomobject]@{DisplayName='Sunshine';PSChildName=$code;WindowsInstaller=1},
    [pscustomobject]@{DisplayName='Tailscale';PSChildName='{22222222-2222-2222-2222-222222222222}';WindowsInstaller=1},
    [pscustomobject]@{DisplayName='Sunshine unrelated utility';PSChildName='{33333333-3333-3333-3333-333333333333}';WindowsInstaller=1}
)
$plan=@(Get-SunshineUninstallPlan $products)
if ($plan.Count -ne 1 -or $plan[0].Target -ne $code -or $plan[0].Kind -ne 'MSI') { throw 'Uninstall scope regression' }
if (@(Get-SunshineUninstallPlan @()).Count -ne 0) { throw 'Absent engine is not a no-op' }
$rejected=$false
try { Get-SunshineUninstallPlan @([pscustomobject]@{DisplayName='Sunshine';PSChildName='bad';WindowsInstaller=0;UninstallString='cmd.exe /c arbitrary'}) } catch {$rejected=$true}
if (!$rejected) { throw 'Untrusted uninstall command accepted' }
[xml]$xml=Get-Content (Join-Path $PSScriptRoot 'PiuLink.Host.Setup\Package.wxs') -Raw
$ns=[Xml.XmlNamespaceManager]::new($xml.NameTable);$ns.AddNamespace('w','http://wixtoolset.org/schemas/v4/wxs')
$action=$xml.SelectSingleNode('//w:Custom[@Action="RemoveStandaloneSunshineOnUninstall"]',$ns)
if ($action.Condition -ne 'Installed AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE') { throw 'Upgrade/repair uninstall guard changed' }
'PASS exact Sunshine MSI selection, Tailscale preservation, absent engine, unsafe command rejection, upgrade/repair guards'
