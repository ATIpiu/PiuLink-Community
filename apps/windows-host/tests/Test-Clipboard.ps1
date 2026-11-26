[CmdletBinding()]
param(
    [string]$ExpectedInviteFile,
    [switch]$ExpectedInviteFromInput,
    [long]$WindowHandle,
    [long]$ClientWindowHandle,
    [switch]$ReproduceOldFailure
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public sealed class ClipboardTestLock : IDisposable {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls, string title);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    readonly ManualResetEvent release = new ManualResetEvent(false);
    readonly Thread thread;
    bool locked;
    public ClipboardTestLock(int milliseconds) {
        thread = new Thread(() => {
            for (int i = 0; i < 100 && !locked; i++) {
                locked = OpenClipboard(IntPtr.Zero);
                if (!locked) Thread.Sleep(20);
            }
            ready.Set();
            if (locked) try { release.WaitOne(milliseconds); } finally { CloseClipboard(); }
        });
        thread.IsBackground = true;
        thread.Start();
        ready.WaitOne();
        if (!locked) { Dispose(); throw new Exception("The test could not own the clipboard."); }
    }
    public void Dispose() { release.Set(); thread.Join(); ready.Dispose(); release.Dispose(); }
}
'@
function Find-Control($container, $property, $value) {
    $container.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new($property,$value))
}
function Find-Id($id) { Find-Control $window ([Windows.Automation.AutomationElement]::AutomationIdProperty) $id }
function Invoke-Control($element) {
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-For($description, [scriptblock]$check) {
    $limit = [DateTime]::UtcNow.AddSeconds(8)
    do {
        if (& $check) { return }
        Start-Sleep -Milliseconds 60
    } while ([DateTime]::UtcNow -lt $limit)
    throw "Timed out waiting for $description."
}
function ClipboardText {
    try { [Windows.Forms.Clipboard]::GetText() }
    catch [System.Runtime.InteropServices.ExternalException] { return $null }
}
function With-Lock($duration, [scriptblock]$action) {
    $held = [ClipboardTestLock]::new($duration)
    try { & $action } finally { $held.Dispose() }
}
function Select-Page($id) {
    (Find-Id $id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Check-LogCopy($parent, $openLabel, $dialogTitle, $copyLabel) {
    $open = Find-Control $parent ([Windows.Automation.AutomationElement]::NameProperty) $openLabel
    if (!$open) { throw "Missing log viewer button $openLabel." }
    Invoke-Control $open
    Wait-For 'the log viewer' { [ClipboardTestLock]::FindWindow([IntPtr]::Zero,$dialogTitle) -ne [IntPtr]::Zero }
    $dialog = [Windows.Automation.AutomationElement]::FromHandle([ClipboardTestLock]::FindWindow([IntPtr]::Zero,$dialogTitle))
    if ($dialog.Current.ProcessId -ne $parent.Current.ProcessId) { throw 'Unexpected log viewer owner.' }
    try {
        $box = Find-Control $dialog ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'LogText'
        $value = $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        Wait-For 'displayed log text' { $value.Current.Value.Length -gt 0 }
        $expected = $value.Current.Value
        $button = Find-Control $dialog ([Windows.Automation.AutomationElement]::NameProperty) $copyLabel
        With-Lock 10000 {
            Invoke-Control $button
            Wait-For 'retry feedback on log copy' { $button.Current.Name -match '重试|Retry' }
        }
        Invoke-Control $button
        Wait-For 'all displayed log text' { (ClipboardText) -ceq $expected }
        "PASS $dialogTitle stayed responsive on failure and copied the full displayed text on retry"
    } finally { $dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
}
if (!$WindowHandle) {
    $tray = @(Get-Process PiuLink.Host | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
    if ($tray.Count -ne 1) { throw 'Open the running Host dashboard or pass its window handle.' }
    $WindowHandle = $tray[0].MainWindowHandle.ToInt64()
}
$window = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$WindowHandle)
if ((Get-Process -Id $window.Current.ProcessId).ProcessName -ne 'PiuLink.Host') {throw 'Unexpected window owner.'}
(Find-Id 'OverviewNavButton').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
if ($ExpectedInviteFromInput) {
    $link = [Console]::In.ReadLine().Trim()
} elseif ($ExpectedInviteFile) {
    $link = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ExpectedInviteFile)).Trim()
} else {
    # An explicit disable/re-enable rotates the room while keeping the Sunshine UUID.
    # Derive the current invitation independently of both the clipboard and the UI.
    $exe = (Get-Process -Id $window.Current.ProcessId).Path
    $assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::ChangeExtension($exe,'dll'))
    $credentialType = $assembly.GetType('PiuLink.Host.Services.WindowsCredentialStore',$true)
    $store = [Activator]::CreateInstance($credentialType,$true)
    $credential = $credentialType.GetMethod('Read').Invoke($store,@('PiuLink/AddressInvite/v1'))
    if ($credential.Username -ne 'enabled' -and $credential.Username -notlike 'https://*') {throw 'The Host invitation must already be enabled.'}
    $response = Invoke-WebRequest -NoProxy 'http://127.0.0.1:47989/serverinfo'
    $xml = [xml]::new()
    $xml.Load($response.RawContentStream)
    $hostId = [Guid]$xml.DocumentElement.uniqueid
    $inviteType = $assembly.GetType('PiuLink.Rendezvous.HostInvite')
    $method = $inviteType.GetMethod('FromOwner')
    $arguments = @($credential.Secret,$hostId)
    if ($method.GetParameters().Count -eq 3) {
        $arguments += if ($credential.Username -eq 'enabled') { $inviteType.GetField('Base').GetRawConstantValue() } else { $credential.Username }
    }
    $link = $method.Invoke($null,$arguments).Link
    $credential = $null
}
if ($link -notmatch '^https://[^/]+/piulink/join#[0-9a-f]{64}\.[0-9a-f-]{36}$') { throw 'Unexpected test invitation format.' }
Wait-For 'published invitation' { $script:invite = Find-Id 'InviteButton'; $invite.Current.IsEnabled -and $invite.Current.Name -match '复制' }
$status = Find-Id 'InviteStatus'
$info = Find-Id 'CopyButton'
$summary = (Find-Id 'ConnectionText').Current.Name
if (!$summary -or !$info.Current.IsEnabled) { throw 'Connection summary unavailable.' }
$sentinel = 'PiuLink 剪贴板测试 ' + [Guid]::NewGuid().ToString('N')
[Windows.Forms.Clipboard]::SetText($sentinel)
if ($ReproduceOldFailure) {
    With-Lock 10000 {
        Invoke-Control $invite
        Wait-For 'the original clipboard exception' { $status.Current.Name -like '*CLIPBRD_E_CANT_OPEN*' }
    }
    if ((ClipboardText) -cne $sentinel) { throw 'Unexpected write while clipboard was locked.' }
    'PASS reproduced the original error on the actual Host button'
    return
}

Invoke-Control $info
Wait-For 'the complete connection summary' { (ClipboardText) -ceq $summary }
'PASS connection summary including every line'
With-Lock 850 { Invoke-Control $invite; Wait-For 'the exact invitation after contention' { (ClipboardText) -ceq $link } }
Wait-For 'copy confirmation' { $status.Current.Name -like '*已复制*' }
'PASS one click survives a transient lock and copies the original invitation'

[Windows.Forms.Clipboard]::SetText($sentinel)
With-Lock 12000 {
    Invoke-Control $invite
    Wait-For 'manual fallback' { $script:fallback = Find-Id 'InviteLinkFallback'; $fallback -and $status.Current.Name -like '*手动复制*' }
    if ($fallback.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -cne $link) { throw 'Manual fallback content differs.' }
    $selection = $fallback.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).GetSelection()
    if ($selection.Count -ne 1 -or $selection[0].GetText(-1) -cne $link) { throw 'The fallback did not select the complete invitation.' }
    Invoke-Control $info
    Wait-For 'connection copy failure feedback' { $info.Current.Name -match '重试|Retry' }
}
if ((ClipboardText) -cne $sentinel) { throw 'A failed operation modified the clipboard.' }
$fallback.SetFocus()
if ([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -ne 'InviteLinkFallback') {
    throw 'Manual copy requires focus on the invitation field.'
}
[Windows.Forms.SendKeys]::SendWait('^a')
[Windows.Forms.SendKeys]::SendWait('^c')
Wait-For 'manual keyboard copy of the entire invitation' { (ClipboardText) -ceq $link }
'PASS manual Ctrl+C copies the complete invitation after the clipboard is released'
Invoke-Control $info
Wait-For 'connection copy recovery' { (ClipboardText) -ceq $summary }
'PASS sustained contention, selectable original invite, no failed write, and recovery'

[Windows.Forms.Clipboard]::SetText($sentinel)
With-Lock 850 {
    Invoke-Control $invite
    Invoke-Control $info
    Start-Sleep -Milliseconds 1200
}
if ((ClipboardText) -cne $summary) { throw 'An older click overwrote the latest copy.' }
'PASS competing copy buttons retain the latest selection'
Invoke-Control $info
Wait-For 'initial connection-copy confirmation' { $info.Current.Name -match '已复制|Copied' }
With-Lock 900 {
    Invoke-Control $info
    Invoke-Control $invite
    Start-Sleep -Milliseconds 1100
}
Wait-For 'cleared feedback after a superseded copy' { $info.Current.Name -match '复制连接信息|Copy connection info' }
Wait-For 'the latest invitation after the canceled connection copy' { (ClipboardText) -ceq $link }
'PASS superseding a busy copy also clears earlier success feedback'
if ($ClientWindowHandle) {
    try {
        Select-Page 'WakeOnLanNavButton'
        Wait-For 'the MAC copy button' { $script:macButton = Find-Control $window ([Windows.Automation.AutomationElement]::NameProperty) 'Copy MAC'; $null -ne $macButton }
        With-Lock 10000 {
            Invoke-Control $macButton
            Wait-For 'retry feedback on MAC copy' { $macButton.Current.Name -match '重试|Retry' }
        }
        Invoke-Control $macButton
        $addresses = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object { $_.GetPhysicalAddress().ToString() }
        Wait-For 'an address of a local adapter' {
            $mac = (ClipboardText) -replace '[:-]',''
            $mac -match '^[a-f0-9]{12}$' -and $mac -in $addresses
        }
        'PASS real local adapter MAC copied after contention'
        Select-Page 'DiagnosticsNavButton'
        Check-LogCopy $window 'View logs' 'PiuLink Host logs' 'Copy'
        $client = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$ClientWindowHandle)
        if ((Get-Process -Id $client.Current.ProcessId).ProcessName -ne 'PiuLink.Client') {throw 'Unexpected client process.'}
        $advanced = Find-Control $client ([Windows.Automation.AutomationElement]::NameProperty) '高级串流设置'
        $expander = $advanced.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $collapsed = $expander.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed
        if ($collapsed) { $expander.Expand() }
        try { Check-LogCopy $client '查看客户端日志' 'PiuLink 客户端日志' '复制' }
        finally { if ($collapsed) { $expander.Collapse() } }
    } finally { Select-Page 'OverviewNavButton' }
    $invite = Find-Id 'InviteButton'
    $status = Find-Id 'InviteStatus'
}
Invoke-Control $invite
Wait-For 'the final invitation' { (ClipboardText) -ceq $link -and $status.Current.Name -like '*已复制*' -and !(Find-Id 'InviteLinkFallback') }
'PASS retry hides the fallback and leaves the verified link on the clipboard'
