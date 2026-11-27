using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using PiuLink.Desktop;
using PiuLink.Host.Services;
using PiuLink.Rendezvous;

namespace PiuLink.Host;

public partial class InviteDialog : Window
{
    public static Uri GuideUrl { get; } = new("https://github.com/ATIpiu/PiuLink-Community/tree/main/services/rendezvous#readme");
    private readonly bool _confirmCreation;
    private readonly bool _locked;
    private readonly string? _currentLink;
    private readonly List<PastInvite> _history = [];
    private string _customDraft;
    private bool _initialized;

    internal InviteDialog(bool confirmCreation, string service, bool locked, string? currentLink)
    {
        _confirmCreation = confirmCreation;
        _locked = locked;
        _currentLink = currentLink;
        _customDraft = service == HostInvite.Base ? "" : service;
        InitializeComponent();
        MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height - 36);
        Title = confirmCreation ? "直连邀请" : "打洞服务与邀请历史";
        HeadingText.Text = confirmCreation ? "生成直连邀请？" : "邀请设置";
        if (confirmCreation) {
            HeaderIcon.Symbol = HostSymbol.Shield;
            WarningSection.Visibility = Visibility.Visible;
            ServiceSummary.Text = (service == HostInvite.Base ? "官方服务 · " : "自建服务 · ") + new Uri(service).Authority;
            GuideIntro.Text = "推荐使用 Tailscale，或在自己的 VPS 上部署服务。 ";
            PrimaryAction.Content = "生成邀请";
            Loaded += (_, _) => CancelAction.Focus();
        } else {
            SettingsSection.Visibility = HistorySection.Visibility = Visibility.Visible;
            (service == HostInvite.Base ? Official : Custom).IsChecked = true;
            Official.IsEnabled = Custom.IsEnabled = !locked;
            GuideIntro.Text = "想使用自己的 VPS？ ";
            ServiceHint.Text = locked ? "当前邀请正在使用此服务。关闭邀请后可切换服务。" :
                "客户端会从邀请链接读取服务地址。";
            if (locked) CancelAction.Visibility = Visibility.Collapsed;
            PrimaryAction.Content = locked ? "完成" : "保存设置";
            ShowProvider();
            try { _history.AddRange(InvitePreferences.History()); }
            catch (Exception) { Notify("无法读取此 Windows 用户的邀请历史。"); }
            RenderHistory();
        }
        _initialized = true;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; } };
    }

    private void ProviderChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ShowProvider();
        Feedback.Visibility = Visibility.Collapsed;
        if (Custom.IsChecked == true) ServiceAddress.Focus();
    }

    private void ShowProvider()
    {
        var official = Official.IsChecked == true;
        ServiceAddress.IsReadOnly = _locked || official;
        ServiceAddress.Text = official ? HostInvite.Base : _customDraft;
        if (!_locked) ServiceHint.Text = official ? "客户端会自动使用官方服务。" :
            "填写自己的 HTTPS 域名，可带端口，例如 https://connect.example.org。";
    }

    private void ServiceEdited(object sender, TextChangedEventArgs e)
    {
        if (_initialized && Custom.IsChecked == true) _customDraft = ServiceAddress.Text;
    }

    private void Apply(object sender, RoutedEventArgs e)
    {
        if (!_confirmCreation && !_locked) {
            try { InvitePreferences.Service = Official.IsChecked == true ? HostInvite.Base : ServiceAddress.Text; }
            catch (Exception ex) { Notify(ex.Message); ServiceAddress.Focus(); return; }
        }
        DialogResult = true;
    }

    private void OpenGuide(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(GuideUrl.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { Notify("未能打开浏览器。部署说明位于 GitHub 的 ATIpiu/PiuLink-Community 仓库，目录为 services/rendezvous。"); }
    }

    private void RenderHistory()
    {
        History.ItemsSource = _history.Select(i => new {
            Authority = new Uri(i.Link).Authority, Date = i.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            State = i.Link == _currentLink ? "当前邀请" : "历史记录"
        }).ToArray();
        HistoryCount.Text = $"{_history.Count} / 20";
        EmptyHistory.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        History.Visibility = HistoryActions.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearPrompt.Visibility = Visibility.Collapsed;
    }

    private void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        Copy.IsEnabled = History.SelectedIndex >= 0;
        CopyFallback.Clear();
        CopyFallback.Visibility = Visibility.Collapsed;
    }

    private async void CopyHistory(object sender, RoutedEventArgs e)
    {
        var index = History.SelectedIndex;
        if (index < 0 || index >= _history.Count) return;
        var link = _history[index].Link;
        var result = await DesktopClipboard.CopyAsync(this, link, CancellationToken.None);
        if (!IsVisible || index != History.SelectedIndex || result == CopyResult.Canceled) return;
        if (result == CopyResult.Copied) {
            CopyFallback.Clear();
            CopyFallback.Visibility = Visibility.Collapsed;
            Notify(link == _currentLink ? "当前邀请已复制。" : "历史链接已复制，此邀请可能已关闭。");
        } else {
            CopyFallback.Text = link;
            CopyFallback.Visibility = Visibility.Visible;
            CopyFallback.Focus(); CopyFallback.SelectAll();
            Notify("剪贴板正被占用。释放后可按 Ctrl+C 复制选中的链接。");
        }
    }

    private void ConfirmClear(object sender, RoutedEventArgs e)
    {
        HistoryActions.Visibility = Visibility.Collapsed;
        ClearPrompt.Visibility = Visibility.Visible;
        ClearPrompt.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void KeepHistory(object sender, RoutedEventArgs e)
    {
        ClearPrompt.Visibility = Visibility.Collapsed;
        HistoryActions.Visibility = Visibility.Visible;
        Clear.Focus();
    }
    private void ClearHistory(object sender, RoutedEventArgs e)
    {
        try { InvitePreferences.ClearHistory(); _history.Clear(); RenderHistory(); Notify("本机邀请历史已清空。"); }
        catch (Exception ex) { Notify(ex.Message); }
    }
    private void Notify(string text) { Feedback.Text = text; Feedback.Visibility = Visibility.Visible; }
}
