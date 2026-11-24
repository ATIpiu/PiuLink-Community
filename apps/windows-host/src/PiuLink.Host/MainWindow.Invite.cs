using System.Windows;
using PiuLink.Desktop;
using PiuLink.Host.Services;
using PiuLink.Rendezvous;

namespace PiuLink.Host;

public partial class MainWindow
{
    private const string InviteCredential = "PiuLink/AddressInvite/v1";
    private CancellationTokenSource? _inviteSession;
    private HostInvite? _publishedInvite;
    private TaskCompletionSource<bool>? _punchApproval;

    private async Task<bool> ApprovePunchAsync(string fingerprint, CancellationToken token)
    {
        if (_punchApproval != null) return false;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _punchApproval = pending;
        PunchApproval.Content = $"允许新设备打洞 · {fingerprint[..12]}";
        PunchApproval.Visibility = Visibility.Visible;
        try { return await pending.Task.WaitAsync(TimeSpan.FromSeconds(20),token); }
        catch (TimeoutException) { return false; }
        finally { PunchApproval.Visibility = Visibility.Collapsed; _punchApproval = null; }
    }

    private void ApprovePunch_Click(object sender,RoutedEventArgs e) => _punchApproval?.TrySetResult(true);

    private void RestoreInvite()
    {
        try
        {
            if (_inviteSession == null && new WindowsCredentialStore().Read(InviteCredential) is { Username: "enabled" } stored)
                _ = MaintainInviteAsync(stored.Secret);
        }
        catch (Exception error) { InviteStatus.Text = error.Message; }
    }

    private async void Invite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_publishedInvite is { } invite && _inviteSession is { } session)
            {
                var result = await DesktopClipboard.CopyAsync(this, invite.Link, session.Token);
                if (result == CopyResult.Canceled || session != _inviteSession || invite != _publishedInvite) return;
                if (result == CopyResult.Copied)
                {
                    HideInviteLink();
                    InviteStatus.Text = "邀请链接已复制。请在客户端的「添加电脑」中粘贴。";
                }
                else
                {
                    InviteStatus.Text = "暂时无法写入剪贴板。可重试，或选中下方链接手动复制。";
                    InviteLinkFallback.Text = invite.Link;
                    InviteLinkFallback.Visibility = Visibility.Visible;
                    InviteLinkFallback.Focus();
                    InviteLinkFallback.SelectAll();
                }
            }
            else if (_inviteSession == null)
            {
                var owner = InvitePublisher.NewOwner();
                new WindowsCredentialStore().Write(InviteCredential, new("enabled", owner));
                _ = MaintainInviteAsync(owner);
            }
        }
        catch (Exception error) { InviteStatus.Text = error.Message; }
    }

    private void HideInviteLink()
    {
        InviteLinkFallback.Clear();
        InviteLinkFallback.Visibility = Visibility.Collapsed;
    }

    private async Task MaintainInviteAsync(string owner)
    {
        using var session = new CancellationTokenSource();
        _inviteSession = session;
        var token = session.Token;
        Task? punch = null;
        StopInviteButton.Visibility = Visibility.Visible;
        InviteButton.IsEnabled = false;
        InviteStatus.Text = "正在登记这台电脑的直连地址…";
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var invite = await InvitePublisher.PublishAsync(owner, token);
                    token.ThrowIfCancellationRequested();
                    if(punch == null || punch.IsCompleted)
                        punch = DirectInviteHost.RunAsync(owner,invite,ApprovePunchAsync,s => _log.WriteAsync(s),token);
                    if (_publishedInvite != invite)
                    {
                        HideInviteLink();
                        InviteStatus.Text = "邀请已开启。首次连接仍需在 Host 确认配对。";
                    }
                    _publishedInvite = invite;
                    InviteButton.Content = "复制直连邀请链接";
                    InviteButton.IsEnabled = true;
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    _publishedInvite = null;
                    HideInviteLink();
                    InviteButton.IsEnabled = false;
                    InviteStatus.Text = "登记失败，请检查 Sunshine 和网络。30 秒后自动重试。";
                }
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally {
            session.Cancel();
            if (punch != null) try { await punch; } catch (Exception ex) { await _log.WriteAsync("P2P listener ended: " + ex.GetType().Name); }
            if (ReferenceEquals(_inviteSession, session)) _inviteSession = null;
        }
    }

    private void StopInvite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new WindowsCredentialStore().Write(InviteCredential, new("disabled", "-"));
            _inviteSession?.Cancel();
            _inviteSession = null;
            _publishedInvite = null;
            HideInviteLink();
            StopInviteButton.Visibility = Visibility.Collapsed;
            InviteButton.Content = "开启直连邀请";
            InviteButton.IsEnabled = true;
            InviteStatus.Text = "已停止登记，旧链接将在 90 秒内过期。再次开启会生成新链接。";
        }
        catch (Exception error) { InviteStatus.Text = error.Message; }
    }
}
