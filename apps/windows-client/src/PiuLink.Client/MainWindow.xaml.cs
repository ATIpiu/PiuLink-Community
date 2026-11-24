using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Storage;
using PiuLink.Client.Core.Streaming;

namespace PiuLink.Client;

public partial class MainWindow : Window
{
    private readonly JsonHostRepository _repository;
    private readonly HostCompanionClient _companion = new();
    private HostDisplayInventory? _displayInventory;
    private CancellationTokenSource? _launchCancellation;
    private CancellationTokenSource? _automaticReconnectCancellation;
    private IStreamSession? _activeStream;
    private PiuLinkEngineChannel? _activeEngineChannel;
    private PiuLinkEngineTelemetry? _lastEngineTelemetry;
    private HostProfile? _activeHost;
    private string? _activeApp;
    private Task? _streamFinished;
    private bool _launchingQt;
    private bool _saveFailed;
    private bool _userRequestedDisconnect;
    private bool _restarting;
    private readonly HostProbeService _probeService = new();
    private readonly LanHostDiscoveryService _discoveryService = new();
    private readonly MoonlightEngineLocator _engineLocator = new();
    private readonly MoonlightQtEngineLocator _qtEngineLocator = new();
    private CancellationTokenSource? _refreshCancellation;
    private MoonlightEngineLocation _engineLocation = new(false, null, "Not checked.");
    private MoonlightQtEngineLocation _qtEngineLocation = new(false, null, "Not checked.");
    private MoonlightProcessService? _moonlight;
    private MoonlightQtCommandService? _qtMoonlight;
    private bool _closed;
    private bool _updatingMetadata;
    private bool _hostsLoaded;
    private bool _restoringProfile;
    private bool _closingAfterSave;
    private bool _savingBeforeClose;
    private readonly Dictionary<Guid, (StreamProfile Profile, string? Display)> _drafts = [];
    private readonly SemaphoreSlim _hostMutation = new(1, 1);
    private readonly HashSet<Guid> _pairingHosts = [];
    private readonly HashSet<Guid> _streamingHosts = [];
    private int _virtualGamepadButtons;
    private int _virtualLeftX;
    private int _virtualLeftY;
    private int _virtualRightX;
    private int _virtualRightY;
    private int _virtualLeftTrigger;
    private int _virtualRightTrigger;

    public MainWindow() : this(new JsonHostRepository()) { }
    public MainWindow(JsonHostRepository repository)
    {
        _repository = repository;
        InitializeComponent();
        StreamSurface.MousePosition += (x,y,w,h) => _activeStream?.MousePosition(x,y,w,h);
        StreamSurface.MouseDelta += (x,y) => _activeStream?.MouseMove(x,y);
        StreamSurface.MouseButton += (action,button) => _activeStream?.MouseButton(action,button);
        StreamSurface.StreamMouseWheel += amount => _activeStream?.Scroll(amount);
        StreamSurface.StreamMouseHWheel += amount => _activeStream?.HScroll(amount);
        StreamSurface.Keyboard += (key,action,modifiers) => _activeStream?.Keyboard(key,action,modifiers);
        DataContext = this;
        foreach (var combo in new[] { ResolutionCombo, FpsCombo, BitrateCombo, DisplayModeCombo, CodecCombo, AudioConfigCombo, CaptureSystemKeysCombo, ColorRangeCombo, GamepadDeadzoneCombo, HostDisplayCombo, DisplayScaleCombo })
            combo.SelectionChanged += StreamSetting_Changed;
        foreach (var check in new[] { GatherWindowsCheckBox, HdrCheckBox, DesktopMouseCheckBox, AutoBitrateCheckBox, ContentAdaptiveCheckBox, PerformanceOverlayCheckBox,
                     VsyncCheckBox, FramePacingCheckBox, MultiControllerCheckBox, PlayAudioOnHostCheckBox, MuteOnFocusLossCheckBox,
                     BackgroundGamepadCheckBox, SwapGamepadFaceButtonsCheckBox, VirtualGamepadEnabledCheckBox, Yuv444CheckBox }) {
            check.Checked += StreamSetting_Changed;
            check.Unchecked += StreamSetting_Changed;
        }
        UpdateBitrateModeUi();
    }

    public ObservableCollection<HostProfile> Hosts { get; } = [];

    public ObservableCollection<string> Applications { get; } = [];

    private HostProfile? SelectedHost => HostsList.SelectedItem as HostProfile;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ClientLog.WriteAsync("Client started");
        _engineLocation = _engineLocator.Locate();
        _qtEngineLocation = _qtEngineLocator.Locate();
        if (_engineLocation.IsAvailable && _engineLocation.ExecutablePath is not null)
        {
            _moonlight = new MoonlightProcessService(_engineLocation.ExecutablePath);
        }
        if (_qtEngineLocation.IsAvailable && _qtEngineLocation.ExecutablePath is not null)
        {
            _qtMoonlight = new MoonlightQtCommandService(_qtEngineLocation.ExecutablePath);
        }

        if (_moonlight is not null && _qtMoonlight is not null)
        {
            EngineStatusText.Text = "串流组件已就绪";
            EngineStatusBorder.Background = (Brush)FindResource("SuccessFillBrush");
        }
        else if (_moonlight is not null)
        {
            EngineStatusText.Text = "缺少配对组件，请修复安装后再配对。";
            EngineStatusBorder.Background = (Brush)FindResource("ErrorFillBrush");
        }
        else
        {
            EngineStatusText.Text = "缺少串流组件，请修复安装。";
            EngineStatusBorder.Background = (Brush)FindResource("ErrorFillBrush");
        }

        try
        {
            var hosts = await _repository.LoadAsync();
            if (_closed) return;
            foreach (var host in hosts.OrderByDescending(item => item.LastConnectedAt)
                         .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Hosts.Add(host);
            }

            _hostsLoaded = true;
            if (Hosts.Count > 0)
            {
                HostsList.SelectedIndex = 0;
                var diagnosticApp = Environment.GetEnvironmentVariable("PIULINK_DEBUG_AUTOSTART_APP");
                if (!string.IsNullOrWhiteSpace(diagnosticApp) && SelectedHost is { } diagnosticHost)
                {
                    var app = diagnosticApp.Trim();
                    await ClientLog.WriteAsync($"Diagnostic autostart requested for {diagnosticHost.Name}, app {app}");
                    _ = StartAppAsync(diagnosticHost, app);
                }
            }
            else
            {
                UpdateSelectionState();
            }
        }
        catch (Exception exception)
        {
            if (_closed) return;
            MessageBox.Show(
                this,
                $"无法读取本地主机列表。\n\n{exception.Message}",
                "PiuLink Client",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        new LogViewerWindow { Owner = this }.ShowDialog();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closingAfterSave && _hostsLoaded) {
            e.Cancel = true;
            if (_savingBeforeClose) return;
            _savingBeforeClose = true;
            try {
                await SaveDraftsAsync();
                _closed = true;
                CancelAutomaticReconnect();
                _userRequestedDisconnect = true;
                if (!_launchingQt) _launchCancellation?.Cancel();
                _activeStream?.Stop();
                if (_streamFinished != null) await _streamFinished;
                _closingAfterSave = true;
                Close();
            }
            catch (Exception error) { SetSaveError("保存或关闭失败，窗口保持打开：" + error.Message); }
            finally { _savingBeforeClose = false; }
            return;
        }
        _closed = true;
        _launchCancellation?.Cancel();
        try { _activeStream?.Stop(); } catch (InvalidOperationException) { }
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
    }

    private void ShowAddHost_Click(object sender, RoutedEventArgs e)
    {
        AddHostPanel.Visibility = Visibility.Visible;
        AddHostError.Visibility = Visibility.Collapsed;
        HostNameInput.Text = string.Empty;
        HostAddressInput.Text = string.Empty;
        HostNameInput.Focus();
    }

    private void CancelAddHost_Click(object sender, RoutedEventArgs e)
    {
        AddHostPanel.Visibility = Visibility.Collapsed;
    }

    private async void DiscoverHosts_Click(object sender, RoutedEventArgs e)
    {
        DiscoverHostsButton.IsEnabled = false;
        DiscoverHostsButton.Content = "扫描中…";
        try
        {
            var discovered = await _discoveryService.DiscoverAsync();
            await MergeDiscoveredHostsAsync(discovered);
        }
        catch (Exception exception) { ScreenStatusText.Text = "局域网发现失败：" + exception.Message; }
        finally { DiscoverHostsButton.Content = "发现"; DiscoverHostsButton.IsEnabled = true; }
    }

    private async Task MergeDiscoveredHostsAsync(IReadOnlyList<DiscoveredHost> discovered)
    {
        HostProfile[] additions;
        await _hostMutation.WaitAsync();
        try
        {
            if (_closed) return;
            if (!_hostsLoaded)
                throw new InvalidOperationException("主机列表尚未成功读取，请恢复原列表后再保存发现结果。");
            additions = discovered.Where(item => !Hosts.Any(host =>
                string.Equals(HostEndpoint.Parse(host.Address).Host, item.Address, StringComparison.OrdinalIgnoreCase)))
                .Select(item => new HostProfile(Guid.NewGuid(), item.Name, item.Address, DateTimeOffset.UtcNow)).ToArray();
            if (additions.Length > 0)
            {
                await _repository.SaveAsync(WithDrafts(Hosts.Concat(additions)));
                foreach (var host in additions) Hosts.Add(host);
                HostsList.SelectedItem = additions[0];
            }
        }
        finally { _hostMutation.Release(); }
        ScreenStatusText.Text = discovered.Count == 0 ? "局域网内未发现 PiuLink Host / Sunshine。" :
            additions.Length == 0 ? $"发现 {discovered.Count} 台电脑，均已在列表中。" : $"已发现并添加 {additions.Length} 台电脑。";
    }

    private async void SaveHost_Click(object sender, RoutedEventArgs e)
    {
        await _hostMutation.WaitAsync();
        try
        {
            if (_closed) return;
            if (!_hostsLoaded)
                throw new InvalidOperationException("主机列表尚未成功读取，暂时不能保存，以免覆盖原记录。请修复读取错误后重新打开客户端。");
            var input = HostAddressInput.Text.Trim();
            var invite = PiuLink.Rendezvous.HostInvite.Parse(input);
            var endpoint = await HostEndpoint.ResolveAsync(input);
            var address = endpoint.MoonlightTarget;
            var existing = Hosts.FirstOrDefault(h => string.Equals(h.InviteLink, invite?.Link, StringComparison.Ordinal) && invite != null)
                ?? Hosts.FirstOrDefault(h => string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase) && (invite == null || h.InviteLink == null));
            if (existing != null && invite == null)
            {
                throw new InvalidOperationException("这台电脑已经在列表中。 ");
            }

            var displayName = string.IsNullOrWhiteSpace(HostNameInput.Text)
                ? endpoint.Host
                : HostNameInput.Text.Trim();
            if (displayName.Length > 80)
            {
                throw new InvalidOperationException("显示名称不能超过 80 个字符。 ");
            }

            var profile = existing == null
                ? new HostProfile(Guid.NewGuid(), displayName, address, DateTimeOffset.UtcNow, InviteLink: invite?.Link)
                : existing with { Address = address, InviteLink = invite?.Link };
            await _repository.SaveAsync(WithDrafts(existing == null ? Hosts.Append(profile) : Hosts.Select(h => h.Id == profile.Id ? profile : h)));
            if (_closed) return;
            if (existing == null) Hosts.Add(profile);
            else Hosts[Hosts.IndexOf(existing)] = profile;
            AddHostPanel.Visibility = Visibility.Collapsed;
            HostsList.SelectedItem = profile;
        }
        catch (Exception exception)
        {
            if (_closed) return;
            AddHostError.Text = exception.Message.Trim();
            AddHostError.Visibility = Visibility.Visible;
        }
        finally { _hostMutation.Release(); }
    }

    private async void HostsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || _updatingMetadata) return;
        if (_activeStream is null) _launchCancellation?.Cancel();
        _displayInventory = null;
        _restoringProfile = true;
        HostDisplayCombo.Items.Clear();
        _restoringProfile = false;
        _refreshCancellation?.Cancel();
        SetRefreshBusy(false);
        Applications.Clear();
        PairingPanel.Visibility = Visibility.Collapsed;
        ScreenStatusText.Text = "正在读取这台电脑的屏幕…";
        UpdateStreamAction();
        UpdateSelectionState();
        BeginDisplayInventoryLoad();
        if (SelectedHost is not null) await RefreshSelectedHostAsync();
    }

    private void BeginDisplayInventoryLoad()
    {
        // A display selection belongs to the previous Host until its inventory
        // arrives. Keep the saved value visible, but don't let it be edited or
        // imply that window gathering is available during this short gap.
        HostDisplayCombo.IsEnabled = false;
        GatherWindowsCheckBox.IsEnabled = false;
        GatherWindowsCheckBox.ToolTip = "正在读取当前 Host 的显示器能力…";
        DisplayCapabilityHint.Text = "正在读取当前 Host 的显示器和窗口集中能力…";
    }

    private void UpdateSelectionState()
    {
        MoreHostActions.IsChecked = false;
        var host = SelectedHost;
        var hasSelection = host is not null;
        EmptyStatePanel.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        HostDetailPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (host is null)
        {
            return;
        }

        var endpoint = HostEndpoint.Parse(host.Address);
        SettingsSaveStatus.SetResourceReference(ForegroundProperty, "SuccessBrush");
        _saveFailed = false;
        SettingsSaveStatus.Text = "已保存；下次启动时使用这些参数。";
        SelectedHostName.Text = host.Name;
        SelectedHostAddress.Text = host.Address;
        ConnectionKindText.Text = host.InviteLink != null ? "直连邀请" : endpoint.IsTailscaleAddress ? "TAILSCALE" : "LAN / 手动地址";
        ReachabilityDot.Fill = (Brush)FindResource("SubtleBrush");
        ReachabilityText.Text = "尚未检测";
        AppsHintText.Text = "点击刷新以读取 Sunshine 应用列表。";
        PairHostButton.IsEnabled = _qtMoonlight is not null;
        WakeHostButton.IsEnabled = !string.IsNullOrWhiteSpace(host.MacAddress);
        RestoreProfile(_drafts.TryGetValue(host.Id, out var draft) ? draft.Profile : host.StreamSettings ?? StreamProfile.Default);
    }

    private async void RefreshHost_Click(object sender, RoutedEventArgs e) =>
        await RefreshSelectedHostAsync();

    private async Task RefreshSelectedHostAsync()
    {
        if (_closed) return;
        var host = SelectedHost;
        if (host is null)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;
        SetRefreshBusy(true);
        Applications.Clear();

        try
        {
            var endpoint = await HostEndpoint.ResolveAsync(host.InviteLink ?? host.Address, host.Address, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SelectedHostAddress.Text = endpoint.MoonlightTarget;
            await ClientLog.WriteAsync($"Refreshing host {host.Name} at {endpoint.MoonlightTarget}");
            ReachabilityText.Text = "正在连接 Sunshine…";
            ReachabilityDot.Fill = (Brush)FindResource("SubtleBrush");
            var probe = await _probeService.ProbeAsync(endpoint, TimeSpan.FromSeconds(3), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!probe.IsReachable)
            {
                await ClientLog.WriteAsync($"Host probe failed for {host.Name}: {probe.Detail}");
                ReachabilityDot.Fill = (Brush)FindResource("ErrorBrush");
                ReachabilityText.Text = "无法连接";
                AppsHintText.Text = "请确认主机在线、Sunshine 已启动，并检查地址或 Tailscale 状态。";
                return;
            }

            ReachabilityDot.Fill = (Brush)FindResource("SuccessBrush");
            ReachabilityText.Text = "在线";
            ReachabilityText.ToolTip = $"连接检测耗时 {Math.Max(1, probe.Latency?.TotalMilliseconds ?? 1):0} ms（不代表串流延迟）";

            try {
                using var identity = GameStreamIdentity.LoadOrCreate();
                var info = await new GameStreamHttpClient(endpoint, identity).GetHostInfoAsync(cancellationToken);
                var macAddress = info.MacAddress ?? await LanMacAddressResolver.ResolveAsync(endpoint.Host, cancellationToken);
                if (host.Address != endpoint.MoonlightTarget || (!string.IsNullOrWhiteSpace(macAddress) && !string.Equals(host.MacAddress, macAddress, StringComparison.OrdinalIgnoreCase))) {
                    var updated = host with { Address = endpoint.MoonlightTarget, MacAddress = macAddress ?? host.MacAddress };
                    await _hostMutation.WaitAsync(cancellationToken);
                    try {
                        var index = Hosts.IndexOf(host);
                        if (index >= 0) {
                            await _repository.SaveAsync(WithDrafts(Hosts.Select(item => item.Id == host.Id ? updated : item)), cancellationToken);
                            _updatingMetadata = true;
                            try { Hosts[index] = updated; HostsList.SelectedItem = updated; }
                            finally { _updatingMetadata = false; }
                            host = updated;
                        }
                    } finally { _hostMutation.Release(); }
                }
                WakeHostButton.IsEnabled = !string.IsNullOrWhiteSpace(host.MacAddress);
            } catch (OperationCanceledException) { throw; }
            catch (Exception error) { await ClientLog.WriteAsync("Unable to cache Host MAC address: " + error.Message); }

            if (_qtMoonlight is null)
            {
                AppsHintText.Text = _qtEngineLocation.Detail;
                return;
            }

            AppsHintText.Text = "正在读取应用列表…";
            // Moonlight's CLI allows up to 30 seconds to discover the computer
            // and another 10 seconds to receive its application list.
            var result = await _qtMoonlight.ListAppsAsync(endpoint, TimeSpan.FromSeconds(45), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
            {
                var detail = FriendlyMoonlightError(result.StandardError, result.StandardOutput);
                await ClientLog.WriteAsync($"Application list failed for {host.Name}: {detail}");
                AppsHintText.Text = detail.Contains("not been paired", StringComparison.OrdinalIgnoreCase)
                    ? "主机在线但尚未配对。点击右上角“配对”。"
                    : $"无法读取应用：{detail}";
                return;
            }

            foreach (var app in MoonlightProcessService.ParseAppList(result.StandardOutput).OrderBy(name => {
                var rank = Array.IndexOf(host.RecentApplications ?? [], name); return rank < 0 ? int.MaxValue : rank; }))
            {
                Applications.Add(app);
            }

            try {
                var inventory = await _companion.GetInventoryAsync(endpoint, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_closed || SelectedHost?.Id != host.Id) return;
                _displayInventory = inventory;
                RenderDisplays(_drafts.TryGetValue(host.Id, out var displayDraft) ? displayDraft.Display : host.DisplayId);
            } catch (OperationCanceledException) { throw; }
            catch (Exception error) {
                HostDisplayCombo.IsEnabled = false;
                GatherWindowsCheckBox.IsEnabled = false;
                GatherWindowsCheckBox.ToolTip = "无法读取当前 Host 的显示器能力。";
                DisplayCapabilityHint.Text = "无法读取当前 Host 的显示器能力；请检查 Host 连接后重试。";
                ScreenStatusText.Text = "屏幕接口不可用：" + error.Message;
            }
            AppsHintText.Text = Applications.Count == 0
                ? "Sunshine 没有返回可用应用。"
                : $"已读取 {Applications.Count} 个应用。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer refresh or window shutdown replaced this operation.
        }
        catch (Exception exception)
        {
            await ClientLog.WriteAsync($"Host refresh failed for {host.Name}: {exception}");
            if (cancellationToken.IsCancellationRequested || _closed) return;
            ReachabilityDot.Fill = (Brush)FindResource("ErrorBrush");
            ReachabilityText.Text = "检测失败";
            AppsHintText.Text = exception.Message;
        }
        finally
        {
            if (!_closed && !cancellationToken.IsCancellationRequested)
            {
                SetRefreshBusy(false);
            }
        }
    }

    private async void PairHost_Click(object sender, RoutedEventArgs e)
    {
        var host = SelectedHost;
        if (host is null || _qtMoonlight is null)
        {
            return;
        }

        if (!_pairingHosts.Add(host.Id)) return;
        PairHostButton.IsEnabled = false;

        try
        {
            var endpoint = await HostEndpoint.ResolveAsync(host.InviteLink ?? host.Address, host.Address);
            var pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            PairingPinText.Text = pin;
            PairingPanel.Visibility = Visibility.Visible;
            await ClientLog.WriteAsync($"Qt Moonlight manual pairing started for {host.Name} at {endpoint.MoonlightTarget}");
            using var pairingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            var result = await _qtMoonlight.PairAsync(endpoint, pin, TimeSpan.FromSeconds(65), pairingCancellation.Token);
            await ClientLog.WriteAsync($"Pairing finished for {host.Name} with exit code {result.ExitCode}: {result.StandardError.Trim()}");
            if (!_closed && SelectedHost?.Id == host.Id) {
                PairingPanel.Visibility = Visibility.Collapsed;
                PairingPinText.Text = "";
                if (result.ExitCode == 0) await RefreshSelectedHostAsync();
                else AppsHintText.Text = "配对未完成，请重新配对并在 Host 确认新的 PIN。";
            }
        }
        catch (Exception exception)
        {
            await ClientLog.WriteAsync($"Pairing failed for {host.Name}: {exception}");
            if (!_closed) MessageBox.Show(this, exception.Message, "无法开始配对", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally {
            _pairingHosts.Remove(host.Id);
            if (!_closed && SelectedHost?.Id == host.Id) {
                PairingPanel.Visibility = Visibility.Collapsed;
                PairingPinText.Text = "";
                PairHostButton.IsEnabled = _qtMoonlight is not null;
            }
        }
    }

    private async void WakeHost_Click(object sender, RoutedEventArgs e)
    {
        var host = SelectedHost;
        if (host is null || string.IsNullOrWhiteSpace(host.MacAddress)) return;
        WakeHostButton.IsEnabled = false;
        try {
            await new WakeOnLanService().SendAsync(HostEndpoint.Parse(host.Address), host.MacAddress);
            ReachabilityText.Text = "已发送唤醒数据包";
            await ClientLog.WriteAsync($"Wake-on-LAN packets sent for {host.Name}");
        }
        catch (Exception error) {
            MessageBox.Show(this, error.Message, "无法唤醒电脑", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { if (!_closed && SelectedHost?.Id == host.Id) WakeHostButton.IsEnabled = true; }
    }

    private async void SshHost_Click(object sender, RoutedEventArgs e)
    {
        var host = SelectedHost;
        if (host is null) return;
        SshHostButton.IsEnabled = false;
        try {
            var endpoint = await HostEndpoint.ResolveAsync(host.InviteLink ?? host.Address, host.Address);
            var info = await new HostSshInfoClient().GetAsync(endpoint);
            var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            if (!File.Exists(ssh)) throw new FileNotFoundException("Windows OpenSSH Client 尚未安装。请在“可选功能”中安装 OpenSSH 客户端。", ssh);
            // Start OpenSSH itself; never interpolate network-provided data into cmd.exe /k.
            // -l consumes the username as one argument, and -- terminates option parsing for the host.
            var start = new ProcessStartInfo(ssh) { UseShellExecute = true };
            start.ArgumentList.Add("-p"); start.ArgumentList.Add(info.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-l"); start.ArgumentList.Add(info.Username);
            start.ArgumentList.Add("--"); start.ArgumentList.Add(endpoint.Host);
            Process.Start(start);
            await ClientLog.WriteAsync($"SSH terminal opened for {info.Username}@{endpoint.Host}:{info.Port}");
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开 SSH 终端", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { if (!_closed) SshHostButton.IsEnabled = true; }
    }

    private async void DeleteHost_Click(object sender, RoutedEventArgs e)
    {
        var host = SelectedHost;
        if (host is null)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"从这台电脑删除“{host.Name}”？\n\n这只删除 PiuLink 的本地记录，不会修改 Sunshine。",
            "删除电脑",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await _hostMutation.WaitAsync();
        try
        {
            await _repository.SaveAsync(WithDrafts(Hosts.Where(item => item.Id != host.Id)));
            if (_closed) return;
            Hosts.Remove(host);
            _drafts.Remove(host.Id);
        }
        catch (Exception exception)
        {
            if (!_closed) MessageBox.Show(this, exception.Message, "无法删除电脑", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { _hostMutation.Release(); }
        HostsList.SelectedIndex = Hosts.Count > 0 ? 0 : -1;
        UpdateSelectionState();
    }

    private async void StartApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string appName })
        {
            return;
        }

        var host = SelectedHost;
        if (host is null || _qtMoonlight is null)
        {
            MessageBox.Show(this, "串流内核尚未就绪。", "PiuLink Client", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CancelAutomaticReconnect();
        await StartAppAsync(host, appName);
    }
    private Task StartAppAsync(HostProfile host, string appName, int reconnectAttempt = 0, bool manualSettingsChange = false)
    {
        if (_streamingHosts.Count > 0) return StartAppCoreAsync(host, appName, reconnectAttempt, manualSettingsChange);
        return _streamFinished = StartAppCoreAsync(host, appName, reconnectAttempt, manualSettingsChange);
    }

    private async Task StartAppCoreAsync(HostProfile host, string appName, int reconnectAttempt, bool manualSettingsChange)
    {
        if (_closed || _qtMoonlight is null) return;
        if (_streamingHosts.Count > 0) {
            ScreenStatusText.Text = "已有串流正在连接或运行。请退出当前串流，或使用应用屏幕设置并重连。";
            return;
        }
        _streamingHosts.Add(host.Id);
        CancelPreparationButton.Visibility = Visibility.Visible;
        using var launchCancellation = new CancellationTokenSource();
        _launchCancellation?.Cancel(); _launchCancellation = launchCancellation;
        var token = launchCancellation.Token;
        IStreamSession? launched = null;
        HostEndpoint? sessionEndpoint = null;
        bool scheduleReconnect = false;
        try
        {
            var profile = ReadProfile();
            if (string.Equals(Environment.GetEnvironmentVariable("PIULINK_DEBUG_LOW_LATENCY"), "1", StringComparison.Ordinal))
            {
                // An opt-in diagnostic override. It deliberately does not
                // mutate the saved host profile, so an overloaded Host can be
                // compared against a known-safe baseline in the same WPF path.
                profile = profile with {
                    Resolution = "1920x1080", FramesPerSecond = 60, BitrateKbps = 20000,
                    VideoCodec = "H.264", EnableHdr = false, AutoBitrate = false, ContentAdaptive = false
                };
                await ClientLog.WriteAsync("Diagnostic low-latency profile active: 1920x1080@60, H.264, 20000 Kbps.");
            }

            var endpoint = await HostEndpoint.ResolveAsync(host.InviteLink ?? host.Address, host.Address, token);
            sessionEndpoint = endpoint;
            await ClientLog.WriteAsync($"Stream preparation started for {host.Name}, app {appName}");
            var selectedDisplayId = HostDisplayCombo.SelectedItem is ComboBoxItem choice ? choice.Tag as string : host.DisplayId;
            _drafts[host.Id] = (profile, selectedDisplayId);
            var inventory = await _companion.GetInventoryAsync(endpoint, token);
            token.ThrowIfCancellationRequested();
            var display = HostCompanionClient.ResolveStreamDisplay(inventory, selectedDisplayId, profile);
            ScreenStatusText.Text = "正在准备 Host 屏幕…";
            try { await _companion.PrepareAsync(endpoint, display, profile, token); }
            catch (RetainedDisplayRouteException) when (manualSettingsChange && !token.IsCancellationRequested)
            {
                if (_closed || SelectedHost?.Id != host.Id) return;
                var confirm = MessageBox.Show(this,
                    $"更换串流屏幕需要结束电脑上保留的应用。请确认已保存进度，再重新启动“{appName}”。",
                    "切换串流屏幕", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    ScreenStatusText.Text = "已保留原应用。选择之前的屏幕后可以恢复串流。";
                    return;
                }
                token.ThrowIfCancellationRequested();
                var quit = await _qtMoonlight.QuitAsync(endpoint, TimeSpan.FromSeconds(35), token);
                if (!quit.IsSuccess) throw new IOException("Host 未确认结束原应用，已停止切换屏幕。");
                await _companion.PrepareAsync(endpoint, display, profile, token);
            }
            token.ThrowIfCancellationRequested();
            if (_closed || SelectedHost?.Id != host.Id) return;
            // Select the bitrate once before the handshake. Runtime telemetry is
            // diagnostic only and cannot change the target.
            var launchProfile = profile.AutoBitrate
                ? profile with { BitrateKbps = AutoBitrateController.RecommendedStartupKbps(endpoint, profile.Resolution, profile.FramesPerSecond), ContentAdaptive = false }
                : profile with { ContentAdaptive = false };
            _launchingQt = true;
            try { launched = await _qtMoonlight.LaunchStreamAsync(endpoint, appName, launchProfile, token); }
            finally { _launchingQt = false; }
            if (_closed) { launched.Stop(); await launched.WaitForExitAsync(); return; }
            await ClientLog.WriteAsync($"Qt Moonlight stream process started for {host.Name}, app {appName}");
            PerformanceText.Text = "Qt 引擎性能通道已连接，正在收集首个样本…";
            if (launched is MoonlightQtStreamSession qtSession)
            {
                _activeEngineChannel = qtSession.Channel;
                _lastEngineTelemetry = null;
                _ = ReadEngineTelemetryAsync(qtSession.Channel, launchProfile.BitrateKbps, token);
            }
            _activeStream = launched; _activeHost = host; _activeApp = appName;
            ToggleVirtualGamepadButton.IsEnabled = _activeEngineChannel?.IsConnected == true;
            UpdateVirtualGamepadOverlay();
            CancelPreparationButton.Visibility = Visibility.Collapsed;
            ScreenStatusText.Text = "已打开 Qt Moonlight 串流窗口。修改参数后可点“应用并重连”。";
            UpdateStreamAction();
            await _hostMutation.WaitAsync();
            try
            {
                var current = Hosts.FirstOrDefault(item => item.Id == host.Id) ?? host;
                var updated = current with {
                    LastConnectedAt = DateTimeOffset.UtcNow, StreamSettings = profile, Address = endpoint.MoonlightTarget,
                    DisplayId = selectedDisplayId,
                    RecentApplications = new[] { appName }.Concat(host.RecentApplications ?? []).Distinct().Take(32).ToArray()
                };
                var index = Hosts.IndexOf(current);
                if (index >= 0)
                {
                    await _repository.SaveAsync(WithDrafts(Hosts.Select(item => item.Id == host.Id ? updated : item)));
                    if (_closed) return;
                    var wasSelected = SelectedHost?.Id == host.Id;
                    _updatingMetadata = true;
                    try {
                        Hosts[index] = updated;
                        if (wasSelected) HostsList.SelectedItem = updated;
                    }
                    finally { _updatingMetadata = false; }
                }
            }
            catch (Exception exception)
            {
                if (!_closed) MessageBox.Show(this, exception.Message, "串流已启动，但无法保存连接记录", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _hostMutation.Release(); }
            await launched.WaitForExitAsync();
            await ClientLog.WriteAsync($"Qt Moonlight stream exited for {host.Name} with code {launched.ExitCode}");
            scheduleReconnect = StreamReconnectPolicy.ShouldReconnect(
                launched.ExitCode, _closed || _restarting || _userRequestedDisconnect || token.IsCancellationRequested, reconnectAttempt);
            if (!_closed && SelectedHost?.Id == host.Id)
                ScreenStatusText.Text = launched.ExitCode == 0 || _userRequestedDisconnect ? "串流已结束，可以重新连接。" : scheduleReconnect
                    ? "检测到串流异常中断，正在等待网络恢复…"
                    : $"串流异常退出（{launched.ExitCode}）。请检查 Host 和网络后重试。";
            if (!_closed && SelectedHost?.Id == host.Id && _lastEngineTelemetry is { } finalSample && !scheduleReconnect)
            {
                ScreenStatusText.Text = launched.ExitCode == 0 || _userRequestedDisconnect
                    ? $"串流已结束 · 最后 {finalSample.FramesPerSecond:F1} FPS，网络丢帧 {finalSample.NetworkDroppedPercent:F2}%，RTT {finalSample.RoundTripTimeMs} ms。"
                    : $"串流异常退出（{launched.ExitCode}）· 最后 {finalSample.FramesPerSecond:F1} FPS，网络丢帧 {finalSample.NetworkDroppedPercent:F2}%，RTT {finalSample.RoundTripTimeMs} ms。";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
            if (!_closed && SelectedHost?.Id == host.Id) ScreenStatusText.Text = "已取消连接。";
        }
        catch (Exception exception)
        {
            await ClientLog.WriteAsync($"Stream failed for {host.Name}, app {appName}: {exception}");
            if (!_closed && SelectedHost?.Id == host.Id) ScreenStatusText.Text = "连接失败：" + exception.Message;
        }
        finally {
            if (_userRequestedDisconnect && launched != null)
            {
                try
                {
                    // Pair/list/stream/quit must all use the packaged Qt engine's identity.
                    var cleanup = await _qtMoonlight.QuitAsync(sessionEndpoint!, TimeSpan.FromSeconds(35), CancellationToken.None);
                    if (cleanup.ExitCode != 0) throw new IOException("Qt quit exited with code " + cleanup.ExitCode);
                }
                catch (Exception exception) { await ClientLog.WriteAsync("Host session cleanup failed: " + exception.Message); }
            }
            await ResetVirtualGamepadAsync();
            StreamSurface.SetInputCapture(false);
            if (ReferenceEquals(_activeStream, launched)) { _activeStream = null; _activeHost = null; _activeApp = null; _activeEngineChannel = null; }
            if (ReferenceEquals(_launchCancellation, launchCancellation)) _launchCancellation = null;
            if (launched is not null) await launched.DisposeAsync();
            try { if (sessionEndpoint != null) await _companion.AbortAsync(sessionEndpoint); }
            catch (Exception cleanupError) { await ClientLog.WriteAsync("Display lease cleanup: " + cleanupError.Message); }
            _streamingHosts.Remove(host.Id);
            if (!_closed) StreamOverlay.Visibility = Visibility.Collapsed;
            if (!_closed) VirtualGamepadOverlay.IsOpen = false;
            ToggleVirtualGamepadButton.IsEnabled = false;
            _userRequestedDisconnect = false;
            CancelPreparationButton.Visibility = Visibility.Collapsed;
            if (!_closed) UpdateStreamAction();
            if (scheduleReconnect && !_closed) _ = ReconnectAfterNetworkRecoveryAsync(host, appName, reconnectAttempt);
        }
    }

    private async Task ReadEngineTelemetryAsync(PiuLinkEngineChannel channel, int initialKbps, CancellationToken token)
    {
        var controller = new AutoBitrateController(initialKbps);
        var nextDiagnosticLog = DateTimeOffset.MinValue;
        try
        {
            await channel.SendAsync(PiuLinkEngineCommand.SetBitrate(initialKbps), token);
            await channel.SendAsync(PiuLinkEngineCommand.SetContentAdaptive(false), token);
            await foreach (var sample in channel.ReadTelemetryAsync(token))
            {
                _lastEngineTelemetry = sample;
                controller.Observe(sample, Environment.TickCount64);
                if (DateTimeOffset.UtcNow >= nextDiagnosticLog)
                {
                    await ClientLog.WriteAsync(
                        $"Qt telemetry: fps={sample.FramesPerSecond:F1}; recvMbps={sample.ReceivedMbps:F1}; " +
                        $"rxP25_60sKbps={controller.HistoricalThroughputKbps}; " +
                         $"targetKbps={initialKbps}; engineKbps={sample.EngineTargetKbps}; " +
                        $"networkDrop={sample.NetworkDroppedPercent:F2}%; jitterDrop={sample.JitterDroppedPercent:F2}%; " +
                        $"rtt={sample.RoundTripTimeMs}±{sample.RoundTripVarianceMs}ms; " +
                        $"decode={sample.DecodeTimeMs:F2}ms; render={sample.RenderTimeMs:F2}ms; codec={sample.Codec}; " +
                        $"adaptive={sample.ContentAdaptiveState}");
                    nextDiagnosticLog = DateTimeOffset.UtcNow.AddSeconds(1);
                }
                await Dispatcher.InvokeAsync(() =>
                {
                    PerformanceText.Text =
                        $"视频实收 {sample.ReceivedMbps:F1} Mbps  ·  60秒实收低位 {controller.HistoricalThroughputKbps / 1000.0:F1} Mbps（画面用量）  ·  固定目标 {initialKbps / 1000.0:F1} Mbps\n" +
                        $"Host 目标 {sample.EngineTargetKbps / 1000.0:F1} Mbps  ·  技术上限 {sample.NegotiatedCeilingKbps / 1000.0:F1} Mbps  ·  {sample.FramesPerSecond:F1} FPS  ·  {sample.Codec}\n" +
                        $"丢帧 {sample.NetworkDroppedPercent:F2}%  ·  抖动 {sample.JitterDroppedPercent:F2}%  ·  RTT {sample.RoundTripTimeMs}±{sample.RoundTripVarianceMs} ms\n" +
                        $"解码 {sample.DecodeTimeMs:F2} ms  ·  渲染 {sample.RenderTimeMs:F2} ms  ·  内容自适应 {AdaptiveLabel(sample)}";
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { await ClientLog.WriteAsync("Engine telemetry stopped: " + error); }
    }

    private static string AdaptiveLabel(PiuLinkEngineTelemetry sample) => sample.ContentAdaptiveState switch
    {
        "active-full" => "开启（完整画面）",
        "static-reduced" => "开启（静态降码）",
        "off" => "关闭",
        _ => "等待引擎确认"
    };

    private void CancelAutomaticReconnect()
    {
        _automaticReconnectCancellation?.Cancel();
        _automaticReconnectCancellation?.Dispose();
        _automaticReconnectCancellation = null;
    }

    private async Task ReconnectAfterNetworkRecoveryAsync(HostProfile host, string appName, int attempt)
    {
        CancelAutomaticReconnect();
        using var cancellation = new CancellationTokenSource();
        _automaticReconnectCancellation = cancellation;
        try
        {
            var delay = StreamReconnectPolicy.DelayForAttempt(attempt);
            if (!_closed && SelectedHost?.Id == host.Id)
                ScreenStatusText.Text = $"网络连接已中断，{delay.TotalSeconds:0} 秒后检查并重连（{attempt + 1}/3）…";
            await Task.Delay(delay, cancellation.Token);
            var endpoint = await HostEndpoint.ResolveAsync(host.InviteLink ?? host.Address, host.Address, cancellation.Token);
            var probe = await _probeService.ProbeAsync(endpoint, TimeSpan.FromSeconds(2), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (probe.IsReachable)
            {
                if (!_closed && SelectedHost?.Id == host.Id)
                {
                    ScreenStatusText.Text = "网络已恢复，正在重新连接串流…";
                    await ClientLog.WriteAsync($"Network recovered for {host.Name}; reconnect attempt {attempt + 1}");
                    await StartAppAsync(host, appName, attempt + 1);
                }
                return;
            }

            if (attempt + 1 < 3)
                await ReconnectAfterNetworkRecoveryAsync(host, appName, attempt + 1);
            else if (!_closed && SelectedHost?.Id == host.Id)
                ScreenStatusText.Text = "网络尚未恢复，已停止自动重连。请检查网络后手动连接。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            await ClientLog.WriteAsync($"Automatic reconnect failed for {host.Name}: {error}");
            if (!_closed && SelectedHost?.Id == host.Id)
                ScreenStatusText.Text = "自动重连失败：" + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_automaticReconnectCancellation, cancellation))
                _automaticReconnectCancellation = null;
        }
    }

    private static void SelectTag(ComboBox combo, string tag) {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(value => value.Tag?.ToString() == tag);
        if (item is null) { item = new ComboBoxItem { Content = tag, Tag = tag }; combo.Items.Add(item); }
        combo.SelectedItem = item;
    }
    private void RestoreProfile(StreamProfile profile) {
        _restoringProfile = true;
        try {
        GatherWindowsCheckBox.IsChecked = profile.GatherWindows;
        SelectTag(ResolutionCombo, profile.Resolution); SelectTag(FpsCombo, profile.FramesPerSecond.ToString());
        SelectTag(BitrateCombo, profile.BitrateKbps.ToString()); SelectTag(DisplayModeCombo, profile.DisplayMode);
        SelectTag(CodecCombo, profile.VideoCodec); HdrCheckBox.IsChecked = profile.EnableHdr; DesktopMouseCheckBox.IsChecked = profile.DesktopMouseMode;
        AutoBitrateCheckBox.IsChecked = profile.AutoBitrate; ContentAdaptiveCheckBox.IsChecked = false;
        PerformanceOverlayCheckBox.IsChecked = profile.ShowPerformanceOverlay;
        VsyncCheckBox.IsChecked = profile.EnableVsync; FramePacingCheckBox.IsChecked = profile.FramePacing;
        SelectTag(AudioConfigCombo, profile.AudioConfiguration); MultiControllerCheckBox.IsChecked = profile.MultiController;
        PlayAudioOnHostCheckBox.IsChecked = profile.PlayAudioOnHost; MuteOnFocusLossCheckBox.IsChecked = profile.MuteOnFocusLoss;
        BackgroundGamepadCheckBox.IsChecked = profile.BackgroundGamepad;
        SwapGamepadFaceButtonsCheckBox.IsChecked = profile.SwapGamepadFaceButtons;
        VirtualGamepadEnabledCheckBox.IsChecked = profile.ShowVirtualGamepad;
        Yuv444CheckBox.IsChecked = profile.EnableYuv444; SelectTag(CaptureSystemKeysCombo, profile.CaptureSystemKeys);
        SelectTag(ColorRangeCombo, profile.ColorRange);
        SelectTag(GamepadDeadzoneCombo, profile.GamepadDeadzonePercent.ToString());
        SelectTag(DisplayScaleCombo, profile.DisplayScalePercent.ToString());
        } finally { _restoringProfile = false; }
        UpdateBitrateModeUi();
        UpdateCodecPolicyText();
    }

    private void UpdateCodecPolicyText() {
        if (SelectedHost is null) return;
        try {
            var decision = AutoCodecPolicy.Decide(HostEndpoint.Parse(SelectedHost.Address), ReadProfile());
            CodecPolicyText.Text = decision.Reason switch {
                "auto:tailscale" => "自动：Tailscale 链路在硬解和 Host 支持时优先 AV1，否则 HEVC → H.264。",
                "auto:high-resolution" => "自动：2K/4K 在硬解和 Host 支持时优先 AV1，否则 HEVC → H.264。",
                "auto:local-high-fps" => "自动：局域网高帧率优先低延迟 HEVC，能力不足时回退 H.264。",
                "auto:local-low-latency" => "自动：局域网优先 HEVC，能力不足时回退 H.264。",
                _ => $"手动：固定请求 {ReadProfile().VideoCodec}，不参与自动排序。"
            };
        } catch { CodecPolicyText.Text = "自动编码将在连接前根据网络、分辨率与硬件能力决定。"; }
    }
    private void RenderDisplays(string? selectedId) {
        _restoringProfile = true;
        try {
        HostDisplayCombo.IsEnabled = true;
        HostDisplayCombo.Items.Clear();
        HostDisplayCombo.Items.Add(new ComboBoxItem { Content = "自动选择", Tag = "" });
        foreach (var display in _displayInventory!.Displays)
            HostDisplayCombo.Items.Add(new ComboBoxItem { Content = display.Label, Tag = display.DeviceId });
        var selected = HostDisplayCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == selectedId);
        if (selected is null && !string.IsNullOrEmpty(selectedId)) {
            selected = new ComboBoxItem { Content = "上次屏幕已断开，请重新选择", Tag = selectedId };
            HostDisplayCombo.Items.Add(selected);
        }
        HostDisplayCombo.SelectedItem = selected ?? HostDisplayCombo.Items[0];
        var effectiveDisplay = string.IsNullOrEmpty(selectedId) ? HostCompanionClient.ResolveDisplay(_displayInventory, null)
            : _displayInventory.Displays.FirstOrDefault(d => d.DeviceId == selectedId);
        ScreenStatusText.Text = effectiveDisplay is null && !string.IsNullOrEmpty(selectedId)
            ? "上次屏幕已断开；请明确选择要分享的屏幕后再连接。"
            : effectiveDisplay?.IsVirtual == true
                ? string.IsNullOrEmpty(selectedId) ? "未发现可用物理屏幕；自动选择将使用 Host 虚拟屏幕。" : "已选择虚拟屏幕；连接时应用所选显示模式。"
                : "屏幕列表已同步。自动选择优先 Windows 主物理屏幕。";
        RebuildVideoModes();
        } finally { _restoringProfile = false; }
    }

    private async void ApplyAndReconnect_Click(object sender, RoutedEventArgs e) {
        if (_restarting) return;
        CancelAutomaticReconnect();
        _restarting = true;
        ApplySettingsButton.IsEnabled = false;
        try {
        if (SelectedHost is { } selected) CaptureSettingsDraft(selected);
        await SaveDraftsAsync();
        var process = _activeStream; var host = _activeHost; var app = _activeApp;
        if (process is null || host is null || app is null || host.Id != SelectedHost?.Id) {
            SettingsSaveStatus.Text = "已保存；下次启动时使用这些参数。"; return;
        }
            process.Stop();
            // The existing launch task owns/disposes the process. Wait on its registration instead.
            for (int i = 0; _streamingHosts.Contains(host.Id) && i < 100; i++) await Task.Delay(100);
            if (_streamingHosts.Contains(host.Id)) throw new IOException("串流尚未退出，请退出串流窗口后重试。");
            // StartAppAsync owns the full process lifetime and handles its own failures.
            // The reconnect action must become available again once handoff has started.
            if (!_closed && SelectedHost?.Id == host.Id) _ = StartAppAsync(SelectedHost, app, manualSettingsChange: true);
        } catch (Exception error) { if (!_closed) ScreenStatusText.Text = error.Message; }
        finally { _restarting = false; ApplySettingsButton.IsEnabled = true; }
    }
    private StreamProfile ReadProfile() => new(GetSelectedTag(ResolutionCombo),
        int.Parse(GetSelectedTag(FpsCombo)), int.Parse(GetSelectedTag(BitrateCombo)),
        GetSelectedTag(DisplayModeCombo), GetSelectedTag(CodecCombo), HdrCheckBox.IsChecked == true,
        DesktopMouseCheckBox.IsChecked == true, GatherWindowsCheckBox.IsChecked == true,
        AutoBitrateCheckBox.IsChecked == true, false,
        PerformanceOverlayCheckBox.IsChecked == true, VsyncCheckBox.IsChecked == true,
        FramePacingCheckBox.IsChecked == true, GetSelectedTag(AudioConfigCombo),
        MultiControllerCheckBox.IsChecked == true, PlayAudioOnHostCheckBox.IsChecked == true,
        MuteOnFocusLossCheckBox.IsChecked == true, BackgroundGamepadCheckBox.IsChecked == true,
        SwapGamepadFaceButtonsCheckBox.IsChecked == true, Yuv444CheckBox.IsChecked == true,
        GetSelectedTag(CaptureSystemKeysCombo), VirtualGamepadEnabledCheckBox.IsChecked == true,
        GetSelectedTag(ColorRangeCombo), int.Parse(GetSelectedTag(GamepadDeadzoneCombo)),
        int.Parse(GetSelectedTag(DisplayScaleCombo)));

    private async void StreamSetting_Changed(object sender, RoutedEventArgs e) {
        if (ReferenceEquals(sender, AutoBitrateCheckBox)) UpdateBitrateModeUi();
        if (_closed || _restoringProfile || _updatingMetadata || !_hostsLoaded || SelectedHost is not { } host) return;
        // Clearing controls while changing host is not a user edit.
        var display = HostDisplayCombo.SelectedItem as ComboBoxItem;
        if (ReferenceEquals(sender, HostDisplayCombo) && display is null) return;
        try {
            UpdateCodecPolicyText();
            if (ReferenceEquals(sender, HostDisplayCombo) || ReferenceEquals(sender, ResolutionCombo)) {
                _restoringProfile = true;
                try { RebuildVideoModes(); } finally { _restoringProfile = false; }
            }
            CaptureSettingsDraft(host);
            if (ReferenceEquals(sender, VirtualGamepadEnabledCheckBox)) UpdateVirtualGamepadOverlay();
            _saveFailed = false;
            SettingsSaveStatus.SetResourceReference(ForegroundProperty, "MutedBrush");
            SettingsSaveStatus.Text = "正在保存…";
            await SaveDraftsAsync();
            if (!_closed && SelectedHost?.Id == host.Id)
            {
                _saveFailed = false;
                SettingsSaveStatus.SetResourceReference(ForegroundProperty, "SuccessBrush");
                SettingsSaveStatus.Text = _activeHost?.Id == host.Id && _activeStream is not null
                    ? "已保存；点击“应用并重连”让当前串流使用新参数。"
                    : "已保存；下次启动时使用这些参数。";
            }
        } catch (Exception error) { if (!_closed && SelectedHost?.Id == host.Id) SetSaveError("未保存：" + error.Message); }
    }

    private void UpdateBitrateModeUi() {
        var automatic = AutoBitrateCheckBox.IsChecked == true;
        BitrateCombo.IsEnabled = !automatic;
        AutoBitrateHintText.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SaveDraftsAsync() {
        await _hostMutation.WaitAsync();
        try {
            var updated = WithDrafts(Hosts);
            await _repository.SaveAsync(updated);
        } finally { _hostMutation.Release(); }
    }
    private void CaptureSettingsDraft(HostProfile host) {
        var profile = ReadProfile(); profile.Validate();
        var display = HostDisplayCombo.SelectedItem as ComboBoxItem;
        _drafts[host.Id] = (profile, display is not null ? display.Tag as string
            : _drafts.TryGetValue(host.Id, out var previous) ? previous.Display : host.DisplayId);
    }
    private HostProfile[] WithDrafts(IEnumerable<HostProfile> hosts) => hosts.Select(host =>
        _drafts.TryGetValue(host.Id, out var draft) ? host with { StreamSettings = draft.Profile, DisplayId = draft.Display } : host).ToArray();
    private void CancelPreparation_Click(object sender, RoutedEventArgs e) {
        _launchCancellation?.Cancel();
        CancelPreparationButton.IsEnabled = false;
        ScreenStatusText.Text = "正在取消连接…";
    }

    private void SetSaveError(string text) {
        _saveFailed = true;
        SettingsSaveStatus.SetResourceReference(ForegroundProperty, "ErrorBrush");
        SettingsSaveStatus.Text = text;
    }

    private void UpdateStreamAction() {
        var canApplyToCurrentStream = _activeHost?.Id == SelectedHost?.Id && _activeStream is not null;
        ApplySettingsButton.Content = "应用并重连";
        ApplySettingsButton.Visibility = canApplyToCurrentStream ? Visibility.Visible : Visibility.Collapsed;
        if (!_saveFailed && !SettingsSaveStatus.Text.StartsWith("正在保存", StringComparison.Ordinal))
        {
            SettingsSaveStatus.Text = canApplyToCurrentStream
                ? "已保存；当前串流正在使用已应用的参数。"
                : "已保存；下次启动时使用这些参数。";
        }
        CancelPreparationButton.IsEnabled = true;
    }
    private void ContentColumns_SizeChanged(object sender, SizeChangedEventArgs e) {
        if (SettingsCard is null) return;
        bool compact = e.NewSize.Width < 740;
        ApplicationsColumn.Width = new GridLength(compact ? 1 : 2, GridUnitType.Star);
        ColumnGap.Width = new GridLength(compact ? 0 : 14);
        SettingsColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SettingsCard, compact ? 0 : 2);
        Grid.SetRow(SettingsCard, compact ? 1 : 0);
        SettingsCard.Margin = compact ? new Thickness(0, 14, 0, 0) : new Thickness(0);
    }

    private void RebuildVideoModes() {
        var resolution = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1920x1080";
        var rate = (FpsCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "60";
        var displayScale = (DisplayScaleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
        var deviceId = (HostDisplayCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var selectedDisplay = _displayInventory is null ? null : string.IsNullOrEmpty(deviceId) ? HostCompanionClient.ResolveDisplay(_displayInventory, null) : _displayInventory.Displays.FirstOrDefault(d => d.DeviceId == deviceId);
        bool virtualScreen = selectedDisplay?.IsVirtual == true;
        var modes = virtualScreen ? (selectedDisplay!.Modes ?? []).Select(mode => mode.Resolution).Distinct().ToArray()
            : new[] { "1280x720", "1920x1080", "2560x1440", "3840x2160" };
        if (virtualScreen && !modes.Contains(resolution)) resolution = modes.FirstOrDefault() ?? "1920x1080";
        var rates = virtualScreen ? (selectedDisplay!.Modes ?? []).Where(mode => mode.Resolution == resolution).Select(mode => mode.Fps.ToString()).Distinct().ToArray()
            : new[] { "30", "60", "90", "120" };
        ResolutionCombo.Items.Clear(); FpsCombo.Items.Clear();
        foreach (var mode in modes) ResolutionCombo.Items.Add(new ComboBoxItem { Content = mode.Replace("x", " × "), Tag = mode });
        foreach (var fps in rates) FpsCombo.Items.Add(new ComboBoxItem { Content = fps + " fps", Tag = fps });
        if (!virtualScreen) { SelectTag(ResolutionCombo, resolution); SelectTag(FpsCombo, rate); }
        else {
            ResolutionCombo.SelectedItem = ResolutionCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == resolution)
                ?? ResolutionCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
            FpsCombo.SelectedItem = FpsCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == rate)
                ?? FpsCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        var supportsWindowGathering = _displayInventory?.SupportsWindowGathering == true;
        var canGatherWindows = !virtualScreen && supportsWindowGathering;
        GatherWindowsCheckBox.IsEnabled = canGatherWindows;
        if (!canGatherWindows) GatherWindowsCheckBox.IsChecked = false;
        GatherWindowsCheckBox.ToolTip = virtualScreen
            ? "虚拟屏幕使用合并模式；集中窗口仅适用于物理屏幕。"
            : supportsWindowGathering
                ? "保留其他物理屏幕，将窗口移到所选屏幕。"
                : "当前 Host 尚不支持集中窗口；更新 Host 后可用。";
        var scaleOptions = selectedDisplay?.ScaleOptions ?? [];
        DisplayScaleCombo.Items.Clear();
        DisplayScaleCombo.Items.Add(new ComboBoxItem
        {
            Content = selectedDisplay?.ScalePercent is int current
                ? $"保持 Windows 当前设置（{current}%）"
                : "保持 Windows 当前设置",
            Tag = "0"
        });
        foreach (var scale in scaleOptions)
            DisplayScaleCombo.Items.Add(new ComboBoxItem { Content = scale + "%", Tag = scale.ToString() });
        var supportsDisplayScaling = _displayInventory?.SupportsDisplayScaling == true && scaleOptions.Length > 0;
        DisplayScaleCombo.IsEnabled = supportsDisplayScaling;
        SelectTag(DisplayScaleCombo, supportsDisplayScaling ? displayScale : "0");
        DisplayScaleCombo.ToolTip = supportsDisplayScaling
            ? "修改所选屏幕的 Windows 逐显示器缩放；保持当前不会改动系统设置。"
            : "当前 Host 尚不支持 Windows 屏幕缩放；更新 Host 后可用。";
        DisplayCapabilityHint.Text = virtualScreen
            ? supportsDisplayScaling
                ? "虚拟屏幕：分辨率、帧率和 Windows 缩放由 Host 应用；无法集中窗口。"
                : "虚拟屏幕：分辨率和帧率由 Host 提供；无法集中窗口。"
            : supportsWindowGathering
                ? supportsDisplayScaling
                    ? "物理屏幕：可设置 Windows 缩放，并按需集中窗口。"
                    : "物理屏幕：可按需把窗口集中到这块屏幕。"
                : "物理屏幕：当前 Host 尚不支持窗口集中。";
    }

    private void ToggleVirtualGamepad_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEngineChannel?.IsConnected != true) return;
        VirtualGamepadEnabledCheckBox.IsChecked = !VirtualGamepadOverlay.IsOpen;
    }

    private void HideVirtualGamepad_Click(object sender, RoutedEventArgs e) => VirtualGamepadEnabledCheckBox.IsChecked = false;

    private void UpdateVirtualGamepadOverlay()
    {
        var show = _activeEngineChannel?.IsConnected == true && VirtualGamepadEnabledCheckBox.IsChecked == true;
        VirtualGamepadOverlay.IsOpen = show;
        ToggleVirtualGamepadButton.IsEnabled = _activeEngineChannel?.IsConnected == true;
        ToggleVirtualGamepadButton.Content = show ? "隐藏手柄" : "虚拟手柄";
        if (!show) ResetVirtualGamepadState(send: true);
    }

    private void GamepadButton_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button || !int.TryParse(tag, out var flag)) return;
        button.CaptureMouse();
        _virtualGamepadButtons |= flag;
        SendVirtualGamepadState();
        e.Handled = true;
    }

    private void GamepadButton_Up(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button || !int.TryParse(tag, out var flag)) return;
        if (Mouse.Captured == button) Mouse.Capture(null);
        _virtualGamepadButtons &= ~flag;
        SendVirtualGamepadState();
        e.Handled = true;
    }

    private void Trigger_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender == LeftTriggerSlider) _virtualLeftTrigger = (int)Math.Round(e.NewValue);
        else if (sender == RightTriggerSlider) _virtualRightTrigger = (int)Math.Round(e.NewValue);
        SendVirtualGamepadState();
    }

    private void ResetLeftTrigger_Click(object sender, RoutedEventArgs e) => LeftTriggerSlider.Value = 0;
    private void ResetRightTrigger_Click(object sender, RoutedEventArgs e) => RightTriggerSlider.Value = 0;
    private void ResetTriggers_Click(object sender, RoutedEventArgs e) { LeftTriggerSlider.Value = 0; RightTriggerSlider.Value = 0; }

    private void Stick_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border stick) { stick.CaptureMouse(); SetStick(stick, e.GetPosition(stick)); }
        e.Handled = true;
    }

    private void LeftStick_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Border stick && Mouse.Captured == stick) SetStick(stick, e.GetPosition(stick));
    }

    private void RightStick_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Border stick && Mouse.Captured == stick) SetStick(stick, e.GetPosition(stick));
    }

    private void Stick_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border stick) { if (Mouse.Captured == stick) Mouse.Capture(null); CenterStick(stick); }
        e.Handled = true;
    }

    private void LeftStick_LostMouseCapture(object sender, MouseEventArgs e) { if (sender is Border stick) CenterStick(stick); }
    private void RightStick_LostMouseCapture(object sender, MouseEventArgs e) { if (sender is Border stick) CenterStick(stick); }

    private void SetStick(Border stick, Point position)
    {
        var halfWidth = Math.Max(1, stick.ActualWidth / 2);
        var halfHeight = Math.Max(1, stick.ActualHeight / 2);
        var x = Math.Clamp((position.X - halfWidth) / halfWidth, -1d, 1d);
        var y = Math.Clamp((position.Y - halfHeight) / halfHeight, -1d, 1d);
        var magnitude = Math.Sqrt(x * x + y * y);
        if (magnitude > 1) { x /= magnitude; y /= magnitude; }
        var axisX = (int)Math.Round(x * short.MaxValue);
        var axisY = (int)Math.Round(-y * short.MaxValue);
        if (stick == LeftStickPad) { _virtualLeftX = axisX; _virtualLeftY = axisY; MoveStickKnob(LeftStickKnob, x, y); }
        else { _virtualRightX = axisX; _virtualRightY = axisY; MoveStickKnob(RightStickKnob, x, y); }
        SendVirtualGamepadState();
    }

    private void CenterStick(Border stick)
    {
        if (stick == LeftStickPad) { _virtualLeftX = 0; _virtualLeftY = 0; MoveStickKnob(LeftStickKnob, 0, 0); }
        else { _virtualRightX = 0; _virtualRightY = 0; MoveStickKnob(RightStickKnob, 0, 0); }
        SendVirtualGamepadState();
    }

    private static void MoveStickKnob(FrameworkElement knob, double x, double y) =>
        knob.RenderTransform = new TranslateTransform(x * 35, y * 35);

    private void SendVirtualGamepadState()
    {
        var channel = _activeEngineChannel;
        if (channel?.IsConnected != true) return;
        _ = SendVirtualGamepadStateAsync(channel, _virtualGamepadButtons, _virtualLeftX, _virtualLeftY,
            _virtualRightX, _virtualRightY, _virtualLeftTrigger, _virtualRightTrigger);
    }

    private static async Task SendVirtualGamepadStateAsync(PiuLinkEngineChannel channel, int buttons, int leftX, int leftY,
        int rightX, int rightY, int leftTrigger, int rightTrigger)
    {
        try { await channel.SendAsync(PiuLinkEngineCommand.SetVirtualGamepad(buttons, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger)); }
        catch (Exception) when (!channel.IsConnected) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ResetVirtualGamepadAsync()
    {
        var channel = _activeEngineChannel;
        ResetVirtualGamepadState(send: false);
        if (channel?.IsConnected != true) return;
        await SendVirtualGamepadStateAsync(channel, 0, 0, 0, 0, 0, 0, 0);
    }

    private void ResetVirtualGamepadState(bool send)
    {
        _virtualGamepadButtons = _virtualLeftX = _virtualLeftY = _virtualRightX = _virtualRightY = 0;
        _virtualLeftTrigger = _virtualRightTrigger = 0;
        if (LeftTriggerSlider is not null) LeftTriggerSlider.Value = 0;
        if (RightTriggerSlider is not null) RightTriggerSlider.Value = 0;
        if (LeftStickKnob is not null) MoveStickKnob(LeftStickKnob, 0, 0);
        if (RightStickKnob is not null) MoveStickKnob(RightStickKnob, 0, 0);
        if (send) SendVirtualGamepadState();
    }

    private void DisconnectStream_Click(object sender, RoutedEventArgs e)
    {
        _userRequestedDisconnect = true;
        CancelAutomaticReconnect();
        _launchCancellation?.Cancel();
        _activeStream?.Stop();
    }

    private async Task UpdateEmbeddedStreamStatusAsync(PiuLinkStreamSession session, StreamProfile profile, CancellationToken token)
    {
        var previousFrames=session.RenderedFrames; var previousInputs=session.ForwardedInputEvents; var previousAt=Stopwatch.GetTimestamp();
        try {
            while (!token.IsCancellationRequested && ReferenceEquals(_activeStream, session)) {
                var now=Stopwatch.GetTimestamp(); var seconds=(now-previousAt)/(double)Stopwatch.Frequency;
                var frames=session.RenderedFrames; var inputs=session.ForwardedInputEvents;
                var renderFps=seconds>0?(frames-previousFrames)/seconds:0; var inputRate=seconds>0?(inputs-previousInputs)/seconds:0;
                previousFrames=frames; previousInputs=inputs; previousAt=now;
                StreamFrameText.Text = session.RenderedFrames == 0 ? "· 正在等待视频" : $"· 已呈现 {session.RenderedFrames:N0} 帧 · 输入 {session.ForwardedInputEvents:N0}";
                var adaptive = session.AdaptiveAck switch {
                    2 => "开启",
                    1 => "关闭",
                    0 => "Host 已拒绝（当前编码端不支持）",
                    _ => "等待 Host 回执"
                };
                var process=Process.GetCurrentProcess();
                PerformanceText.Text =
                    $"视频流 {profile.Resolution} · {profile.FramesPerSecond} FPS · {profile.VideoCodec} · {(profile.EnableHdr?"HDR":"SDR")}\n" +
                    $"渲染帧率 {renderFps:F1} FPS · 单帧预算 {(1000.0/profile.FramesPerSecond):F2} ms · 已呈现 {frames:N0} 帧\n" +
                    $"解码器 FFmpeg D3D11VA · 渲染器 D3D11 零拷贝 · 工作集 {process.WorkingSet64/1048576.0:F0} MB\n" +
                    $"当前目标 {session.CurrentBitrateKbps/1000.0:F1} Mbps · 内容自适应 {adaptive}\n" +
                    $"输入发送 {inputRate:F0}/秒 · 累计 {inputs:N0} · 鼠标 {(profile.DesktopMouseMode?"绝对":"相对")}";
                await Task.Delay(500, token);
            }
        } catch (OperationCanceledException) { }
    }

    private void OpenTailscale_Click(object sender, RoutedEventArgs e)
    {
        var tailscaleUi = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tailscale",
            "tailscale-ipn.exe");
        var target = File.Exists(tailscaleUi)
            ? tailscaleUi
            : "https://tailscale.com/download/windows";
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void SetRefreshBusy(bool busy)
    {
        RefreshProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshHostButton.IsEnabled = !busy;
        PairHostButton.IsEnabled = !busy && _qtMoonlight is not null && (SelectedHost is null || !_pairingHosts.Contains(SelectedHost.Id));
        WakeHostButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(SelectedHost?.MacAddress);
    }

    private static string GetSelectedTag(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: not null } item)
        {
            return item.Tag.ToString() ?? throw new InvalidOperationException("Invalid setting value.");
        }

        throw new InvalidOperationException("Select all stream settings.");
    }

    private static string FriendlyMoonlightError(params string[] values)
    {
        var text = values
            .SelectMany(value => value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(line => !line.StartsWith("00:", StringComparison.Ordinal) &&
                                    !line.StartsWith("Qt ", StringComparison.OrdinalIgnoreCase) &&
                                    !line.Contains("DPI awareness", StringComparison.OrdinalIgnoreCase) &&
                                    !line.Contains("SetProcessDpiAwarenessContext", StringComparison.OrdinalIgnoreCase) &&
                                    !line.Contains("qt.conf", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(text) ? "Moonlight returned an unknown error." : text;
    }
}
