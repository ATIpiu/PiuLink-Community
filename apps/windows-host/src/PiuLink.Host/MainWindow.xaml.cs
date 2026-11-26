using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PiuLink.Desktop;
using PiuLink.Host.Core;
using PiuLink.Host.Core.Diagnostics;
using PiuLink.Host.Core.Install;
using PiuLink.Host.Core.Models;
using PiuLink.Host.Core.Sunshine;
using PiuLink.Host.Core.Tailscale;
using PiuLink.Host.Core.WakeOnLan;
using PiuLink.Host.Localization;
using PiuLink.Host.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PiuLink.Host;

public partial class MainWindow : Window
{
    private readonly HostDiagnosticsService _diagnostics = HostDiagnosticsService.CreateDefault();
    private readonly LocalLog _log = new();
    private readonly SunshineApiClient _sunshineApi = SunshineApiClient.CreateForLoopback();
    private bool _virtualApplicationsRegistered;
    private readonly DisplayCompanionServer _displayCompanion;
    private readonly FileTransferPairingStore _fileTransferPairings = new();
    private readonly FileTransferServer _fileTransferServer;
    private readonly SemaphoreSlim _remoteDisplayApplyGate = new(1, 1);
    private readonly SunshineInstallerService _sunshineInstaller =
        SunshineInstallerService.CreateForApplication(AppContext.BaseDirectory);
    private readonly TailscaleControlService _tailscaleControl = TailscaleControlService.CreateDefault();
    private readonly WakeOnLanService _wakeOnLan = WakeOnLanService.CreateDefault();
    private readonly SunshineManagedIdentity _sunshineManagedIdentity = new();
    private readonly VirtualDisplayControlService _virtualDisplayControl = new();
    private readonly DispatcherTimer _windowRecoveryTimer;
    private readonly DispatcherTimer _sessionGuardianTimer;
    private readonly DispatcherTimer _pendingDisplaySettingsTimer;
    private readonly SunshineSessionGuardian _sessionGuardian = new();
    private readonly PhysicalDisplayRecoveryPolicy _physicalDisplayRecovery = new();
    private DisplayTopologyService.Snapshot? _physicalDesktopSnapshot;
    private bool _physicalDisplayRecoveryBusy;
    private bool _remoteDisplaySettingsBusy;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayNetworkStatus;
    private readonly Forms.ToolStripMenuItem _traySunshineStatus;
    private readonly Forms.ToolStripMenuItem _trayAutoStart;
    private readonly Forms.ToolStripMenuItem _trayStartSilently;
    private CancellationTokenSource? _copyFeedbackCancellation;
    private CancellationTokenSource? _exportFeedbackCancellation;
    private CancellationTokenSource? _displayAutoSaveCancellation;
    private HostSnapshot? _snapshot;
    private BundledInstallerStatus? _sunshinePayloadStatus;
    private SunshineDeviceCredential? _sunshineCredential;
    private SunshineOverview? _sunshineOverview;
    private SunshineDisplayState? _displayState;
    private PendingDisplaySettings? _pendingDisplaySettings;
    private PendingDisplayScale? _pendingDisplayScale;
    private bool _pendingDisplayInventoryRefresh;
    private VirtualDisplayDeviceStatus? _virtualDisplayStatus;
    private bool _sunshineAuthenticated;
    private bool _displaySettingsBusy;
    private bool _pendingDisplaySettingsApplyBusy;
    private bool _displayInventoryRefreshAttempted;
    private bool _virtualDisplayBusy;
    private bool _sessionGuardianBusy;
    private bool _tailscaleSignInInProgress;
    private bool _tailscaleSettingsBusy;
    private bool _sunshineConfigurationBusy;
    private bool _sunshineConfigurationLoaded;
    private bool _closeToTrayHintShown;
    private bool _exitRequested;
    private WindowState _lastVisibleWindowState = WindowState.Normal;
    private bool _languageSelectorReady;
    private HostPage _currentPage = HostPage.Overview;

    private sealed record DisplayChoice(
        string Label,
        string? DeviceId,
        SunshineDisplay? Display)
    {
        public override string ToString() => Label;
    }

    private sealed record PendingDisplayScale(string DeviceId, bool IsVirtual, int Percent, DateTimeOffset ExpiresAt);

    private enum HostPage
    {
        Overview,
        PrivateNetwork,
        StreamingHost,
        Displays,
        VirtualDisplay,
        WakeOnLan,
        AdvancedSettings,
        Diagnostics
    }

    public MainWindow()
    {
        InitializeComponent();
        ConfigureArtwork();
        Loaded += (_, _) => RestoreInvite();
        Closed += (_, _) => _inviteSession?.Cancel();
        // One-time cleanup for the retired paired PowerShell server preference.
        using (var preferences = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\ATIPiu Labs\PiuLink Host"))
            preferences.DeleteValue("NativeTerminalEnabled", throwOnMissingValue: false);
        Loaded += async (_, _) => await RefreshSshTerminalStatusAsync();




        _displayCompanion = new DisplayCompanionServer(
            async cancellationToken =>
            {
                var credential = _sunshineCredential;
                if (credential == null) return null;
                var state = await _sunshineApi.GetDisplayStateAsync(credential.Username, credential.Secret, cancellationToken);
                var entry = DisplayRecoveryJournal.Read();
                if (entry == null || state == null) return state;
                return WithActiveCapture(state, entry) with { Displays = WithSessionLayout(state.Displays, entry.Desktop.StreamBounds) };
            },
            ApplyRemoteDisplaySelectionAsync,
            AuthorizeSshDeviceAsync,
            PairFileTransferDeviceAsync);
        _displayCompanion.UpdateLease = async (owner, requestId, action, token) =>
        {
            if (action == "switch") return DisplayRecoveryJournal.BeginSwitch(owner, requestId);
            if (action != "abort" || !await DisplayRecoveryJournal.AbortAsync(owner, requestId, token)) return false;
            var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
            if (credential is null) return false;
            try
            {
                await _sunshineApi.RegisterPreparedDisplayLeaseAsync(requestId, owner,
                    credential.Username, credential.Secret, token, action: "abort");
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest)
            {
                // Sunshine rejects non-live lease mutation while RTSP session_count() > 0 during teardown.
                // Journal is already marked Aborted; the recovery guardian finishes lease revocation once streams reach 0.
                await _log.WriteAsync($"Display lease abort deferred until stream teardown finishes: {DescribeExceptionForLog(error)}");
            }
            return true;
        };
        _displayCompanion.OwnsActiveSession = async (owner, requestId, token) =>
        {
            if (!DisplayRecoveryJournal.OwnsStream(owner, requestId)) return false;
            var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
            return credential != null &&
                await _sunshineApi.GetActiveStreamCountAsync(credential.Username, credential.Secret, token) == 1 &&
                DisplayRecoveryJournal.OwnsStream(owner, requestId);
        };
        _displayCompanion.TextInputAtPointer = SessionInputStatus.TextInputAtPointer;
        _displayCompanion.MicrophoneInUse = DisplayRecoveryJournal.ProbeVirtualMicrophone;
        _displayCompanion.EnsureMicrophone = DisplayRecoveryJournal.EnsureSessionMicrophone;
        _displayCompanion.SessionDisplays = HandleSessionDisplaysAsync;
        _displayCompanion.Start();
        _fileTransferServer = new FileTransferServer(_fileTransferPairings.Validate, _log,
            PairedDisplaySecurity.IsPaired, PairedDisplaySecurity.LoadServerCertificate,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PiuLink"));
        if (!_fileTransferServer.Start())
        {
            _ = _log.WriteAsync("File transfer listener could not bind TCP 48012.");
            Loaded += (_, _) => System.Windows.MessageBox.Show(this,
                "文件接收服务无法监听 TCP 48012。请检查端口占用后重启 Host；串流功能可继续使用。",
                "PiuLink 文件传输", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // Display routing is selected by the connecting client. Discard settings queued by
        // older Host builds so they cannot overwrite the client's next request.
        _pendingDisplaySettings = null;
        HostPreferences.PendingDisplayChange = null;
        _pendingDisplayInventoryRefresh = HostPreferences.PendingDisplayInventoryRefresh;
        LanguageSelector.ItemsSource = LocalizationService.Instance.SupportedLanguages;
        LanguageSelector.SelectedValue = LocalizationService.Instance.SelectedCode;
        _languageSelectorReady = true;
        MoveWindowsToVirtualDisplayCheckBox.IsChecked = false;
        RecoverWindowsWhenDisplayDisappearsCheckBox.IsChecked = HostPreferences.RecoverWindowsWhenDisplayDisappears;
        SessionGuardianCheckBox.IsChecked = HostPreferences.SessionGuardianEnabled;
        SmartDisplayCheckBox.IsChecked = true;
        _windowRecoveryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _windowRecoveryTimer.Tick += WindowRecoveryTimer_Tick;
        SystemEvents.DisplaySettingsChanging += SessionDisplaySettingsChanging;
        _sessionGuardianTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _sessionGuardianTimer.Tick += SessionGuardianTimer_Tick;
        _pendingDisplaySettingsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pendingDisplaySettingsTimer.Tick += PendingDisplaySettingsTimer_Tick;
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = $"ATIPiu Labs · v{version}";
        (_trayIcon, _trayNetworkStatus, _traySunshineStatus, _trayAutoStart, _trayStartSilently) = CreateTrayMenu();
        LocalizationService.Instance.LanguageChanged += Localization_LanguageChanged;
        ApplyLiteralLocalization();
        Loaded += async (_, _) =>
        {
            _sunshineCredential = _sunshineManagedIdentity.TryRead();
            await RefreshAsync();
            await LoadTailscaleSettingsAsync();
            await InspectSunshinePayloadAsync();
            LoadVirtualDisplayStatus();
            if (_snapshot?.Sunshine.State != ComponentState.Missing)
            {
                if (_sunshineCredential is null)
                {
                    await ProvisionSunshineControlAsync(automatic: true);
                }
                if (_sunshineCredential is not null)
                {
                    await LoadSunshineOverviewAsync();
                    if (_sunshineAuthenticated)
                    {
                        await LoadDisplaySettingsAsync();
                        await LoadSunshineConfigurationAsync();
                    }
                }
            }
            _windowRecoveryTimer.Start();
            _sessionGuardianTimer.Start();
            _pendingDisplaySettingsTimer.Start();
            await ConfigureMicrophoneDeviceAsync(automatic: true);
        };
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            LocalizationService.Instance.LanguageChanged -= Localization_LanguageChanged;
            _windowRecoveryTimer.Stop();
            SystemEvents.DisplaySettingsChanging -= SessionDisplaySettingsChanging;
            _sessionGuardianTimer.Stop();
            _pendingDisplaySettingsTimer.Stop();
            _displayAutoSaveCancellation?.Cancel();
            _displayAutoSaveCancellation?.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _displayCompanion.Dispose();
            _fileTransferServer.Dispose();
            _remoteDisplayApplyGate.Dispose();
            _sunshineApi.Dispose();
            _log.Dispose();
        };
    }

    private async Task<bool> AuthorizeSshDeviceAsync(SshAuthorizationRequest request, CancellationToken cancellationToken)
    {
        var defaults = SshDefaults.Read();
        var key = SshKeyAuthorization.Validate(request.PublicKey);
        if (SshKeyAuthorization.IsAuthorized(key, defaults.Username))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorizedStatus = await SshTerminalSetup.ProbeAsync();
            if (authorizedStatus.OpenSsh != "已启动") await SshTerminalSetup.SetOpenSshAsync(true);
            return true;
        }
        var blob = Convert.FromBase64String(key.Split(' ')[1]);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        var approved = await Dispatcher.InvokeAsync(() =>
        {
            ShowFromTray();
            var result = System.Windows.MessageBox.Show(this,
                $"手机“{request.DeviceName}”正在请求 SSH 终端连接。\n\n来源：{request.RemoteAddress}\n设备指纹：{fingerprint}\n\n允许后，这部手机可连接当前 Windows 账户的终端。仅在这是你的设备时允许。",
                "PiuLink 终端连接", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        });
        if (!approved || cancellationToken.IsCancellationRequested) return false;
        var sshStatus = await SshTerminalSetup.ProbeAsync();
        if (sshStatus.OpenSsh != "已启动") await SshTerminalSetup.SetOpenSshAsync(true);
        cancellationToken.ThrowIfCancellationRequested();
        await SshKeyAuthorization.AuthorizeAsync(key, defaults.Username);
        await _log.WriteAsync($"Authorized SSH device {request.DeviceName} from {request.RemoteAddress} ({fingerprint}). TrustedNetwork={IsTrustedPairingRequest(request.RemoteAddress)}");
        return true;
    }

    private async Task<FileTransferPairResponse?> PairFileTransferDeviceAsync(FileTransferPairRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = _fileTransferPairings.Bind(request);
        if (token is null && !request.RefreshOnly)
        {
            var approved = await Dispatcher.InvokeAsync(() =>
            {
                ShowFromTray();
                return System.Windows.MessageBox.Show(this,
                    $"旧文件授权需要重新绑定。允许手机“{request.DeviceName}”使用这台电脑互传文件？\n\n设备证书：{request.CertificateFingerprint}",
                    "PiuLink 文件传输", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (approved) token = _fileTransferPairings.Bind(request, approveLegacy: true);
        }
        return token is null ? null : new("ready", token, FileTransferServer.Port);
    }

    private static bool IsTrustedPairingRequest(string remoteAddress) =>
        IPAddress.TryParse(remoteAddress, out var address) &&
        DisplayCompanionServer.IsTrustedPairingPeer(
            address,
            HostPreferences.AutoPairLan,
            HostPreferences.AutoPairTailnet);

    private void Localization_LanguageChanged(object? sender, EventArgs e) =>
        ApplyLiteralLocalization();

    private void ApplyLiteralLocalization()
    {
        LocalizeDependencyObject(this);
        if (_trayIcon.ContextMenuStrip is not null)
        {
            LocalizeTrayItems(_trayIcon.ContextMenuStrip.Items);
        }
    }

    private static void LocalizeDependencyObject(DependencyObject target)
    {
        if (target is TextBlock textBlock)
        {
            LocalizeProperty(textBlock, TextBlock.TextProperty);
        }

        if (target is ContentControl contentControl)
        {
            LocalizeProperty(contentControl, ContentControl.ContentProperty);
        }

        if (target is HeaderedContentControl headeredContentControl)
        {
            LocalizeProperty(headeredContentControl, HeaderedContentControl.HeaderProperty);
        }

        if (target is FrameworkElement frameworkElement)
        {
            LocalizeProperty(frameworkElement, FrameworkElement.ToolTipProperty);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(target).OfType<DependencyObject>())
        {
            LocalizeDependencyObject(child);
        }
    }

    private static string UiText(string source) => LocalizationService.Instance.TranslateLiteral(source);

    private static void LocalizeProperty(DependencyObject target, DependencyProperty property)
    {
        if (BindingOperations.IsDataBound(target, property) || target.GetValue(property) is not string source)
        {
            return;
        }

        var translated = LocalizationService.Instance.TranslateLiteral(source);
        if (!string.Equals(source, translated, StringComparison.Ordinal))
        {
            target.SetCurrentValue(property, translated);
        }
    }

    private static void LocalizeTrayItems(Forms.ToolStripItemCollection items)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            item.Text = LocalizationService.Instance.TranslateLiteral(item.Text ?? string.Empty);
            if (item is Forms.ToolStripMenuItem menuItem && menuItem.DropDownItems.Count > 0)
            {
                LocalizeTrayItems(menuItem.DropDownItems);
            }
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        SetCheckingState();

        try
        {
            _snapshot = await _diagnostics.CheckAsync();
            Render(_snapshot);
            await _log.WriteAsync(
                $"Diagnostics complete. Ready={_snapshot.IsReady}; Tailscale={_snapshot.Tailscale.State}; Sunshine={_snapshot.Sunshine.State}");
        }
        catch (Exception exception)
        {
            RenderFailure(exception);
            await _log.WriteAsync($"Diagnostics failed: {exception}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void SetCheckingState()
    {
        var subtleBrush = ResourceBrush("SubtleBrush");
        OverallStatus.Text = LocalizationService.Instance.Translate("Status.Checking");
        OverallStatus.Foreground = ResourceBrush("TextBrush");
        OverallStatusDot.Background = subtleBrush;
        HeroStatusGlyph.Text = "…";
        HeroStatusTitle.Text = "Checking this PC";
        HeroStatusDescription.Text = "PiuLink is checking networking and streaming services.";
        DiagnosticsOverallStatus.Text = "Running all checks…";
        DiagnosticsCheckedAt.Text = "Checking now";
        TailscaleGuideStatusText.Text = UiText("Checking");
        TailscaleGuideStatusDetail.Text = UiText("Checking whether Tailscale is installed…");
        _trayNetworkStatus.Text = "Private network · Checking";
        _traySunshineStatus.Text = "Streaming host · Checking";

        foreach (var dot in new[]
                 {
                     TailscaleDot,
                     SunshineDot,
                     TailscaleGuideStatusDot,
                     DiagnosticsTailscaleDot,
                     DiagnosticsSunshineDot,
                     DiagnosticsEndpointDot,
                     DiagnosticsFirewallDot,
                     DiagnosticsGpuDot
                 })
        {
            dot.Background = subtleBrush;
        }
    }

    private void Render(HostSnapshot snapshot)
    {
        var overallBrush = snapshot.IsReady
            ? ResourceBrush("SuccessBrush")
            : ResourceBrush("WarningBrush");

        OverallStatus.Text = LocalizationService.Instance.TranslateLiteral(snapshot.IsReady ? "Ready" : "Needs attention");
        OverallStatus.Foreground = overallBrush;
        OverallStatusDot.Background = overallBrush;
        HeroStatusGlyph.Text = snapshot.IsReady ? "✓" : "!";
        HeroStatusGlyph.Foreground = overallBrush;
        HeroStatusGlyphSurface.Background = ResourceBrush(snapshot.IsReady ? "SuccessFillBrush" : "WarningFillBrush");
        HeroStatusTitle.Text = LocalizationService.Instance.TranslateLiteral(snapshot.IsReady ? "Your host is ready" : "A few things need attention");
        HeroStatusDescription.Text = snapshot.IsReady ? snapshot.DeviceName :
            LocalizationService.Instance.TranslateLiteral("Review the service cards below. PiuLink will point you to the missing setup.");

        DeviceText.Text = snapshot.DeviceName;
        SystemText.Text = $"{snapshot.WindowsVersion} · {snapshot.Architecture}";
        GpuText.Text = snapshot.Gpus.Count == 0
            ? "No display adapter information returned"
            : string.Join(Environment.NewLine, snapshot.Gpus.Select(gpu => $"{gpu.Name} · {gpu.Vendor}"));

        TailscaleStatus.Text = LocalizationService.Instance.TranslateLiteral(snapshot.Tailscale.Summary);
        TailscaleDetail.Text = snapshot.Tailscale.Detail ?? snapshot.TailscaleIp ?? "No additional detail";
        SunshineStatus.Text = LocalizationService.Instance.TranslateLiteral(snapshot.Sunshine.Summary);
        SunshineDetail.Text = snapshot.Sunshine.Detail ?? "No additional detail";
        TailscaleDot.Background = StateBrush(snapshot.Tailscale.State);
        SunshineDot.Background = StateBrush(snapshot.Sunshine.State);
        TailscaleStatus.Foreground = StateBrush(snapshot.Tailscale.State);
        SunshineStatus.Foreground = StateBrush(snapshot.Sunshine.State);
        UpdateTrayStatus(snapshot);

        RenderTailscaleGuide(snapshot);

        var sunshineInstalled = snapshot.Sunshine.State != ComponentState.Missing;
        SunshineNativeStatusDot.Background = StateBrush(snapshot.Sunshine.State);
        SunshineNativeStatusText.Text = UiText(sunshineInstalled ? "Detected" : "Not installed");
        SunshineNativeStatusText.Foreground = StateBrush(snapshot.Sunshine.State);
        SunshineNativeStatusDetail.Text = UiText(sunshineInstalled
            ? "Sunshine is available locally. Connect below to manage it inside PiuLink."
            : "Sunshine was not found. Install the verified bundled component below without leaving PiuLink.");
        SunshineInstallCard.Visibility = sunshineInstalled
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateSunshineConnectionAction(isBusy: false);
        if (!sunshineInstalled)
        {
            _sunshineAuthenticated = false;
            SunshineRefreshButton.IsEnabled = false;
            SubmitPairingPinButton.IsEnabled = false;
            SunshineAppsCountText.Text = "—";
            SunshineClientsCountText.Text = "—";
            SunshineControlStatusText.Text = "Install Sunshine above before connecting to its local API.";
        }

        UpdateSunshineInstallButton();

        var endpointSummary = snapshot.SunshineEndpoint?.Summary ?? "Not checked";
        var firewallSummary = snapshot.Firewall?.Summary ?? "Not checked";
        AdvancedStatusText.Text =
            $"Sunshine endpoint: {endpointSummary}{Environment.NewLine}Windows Firewall: {firewallSummary}";

        TailscaleActionButton.Content = LocalizationService.Instance.TranslateLiteral(snapshot.Tailscale.State switch
        {
            ComponentState.Missing => "Installation guide",
            ComponentState.Ready => "Manage in PiuLink",
            _ => "Finish setup in PiuLink"
        });
        SunshineActionButton.Content = LocalizationService.Instance.TranslateLiteral(snapshot.Sunshine.State == ComponentState.Missing
            ? "Set up in PiuLink"
            : "Manage in PiuLink");

        var addresses = snapshot.LanAddresses?.ToList() ?? [];
        if (snapshot.Tailscale.IsReady && !string.IsNullOrWhiteSpace(snapshot.TailscaleIp))
            addresses.Add("Tailscale · " + snapshot.TailscaleIp);
        ConnectionText.Text = addresses.Count > 0 ? string.Join("   ·   ", addresses) :
            LocalizationService.Instance.Translate("Section.NoAddress");
        CopyButton.Content = LocalizationService.Instance.TranslateLiteral("Copy connection info");
        CopyButton.IsEnabled = true;
        ExportButton.Content = LocalizationService.Instance.TranslateLiteral("Export report");
        ExportButton.IsEnabled = true;

        DiagnosticsOverallStatus.Text = LocalizationService.Instance.TranslateLiteral(snapshot.IsReady
            ? "All required checks passed"
            : "Some checks need your attention");
        DiagnosticsOverallStatus.Foreground = overallBrush;
        DiagnosticsCheckedAt.Text = snapshot.CheckedAt.ToLocalTime().ToString("g");

        RenderDiagnostic(
            snapshot.Tailscale,
            DiagnosticsTailscaleDot,
            DiagnosticsTailscaleStatus,
            DiagnosticsTailscaleDetail,
            "The private network was not checked.",
            snapshot.TailscaleIp ?? "No private address was returned.");
        RenderDiagnostic(
            snapshot.Sunshine,
            DiagnosticsSunshineDot,
            DiagnosticsSunshineStatus,
            DiagnosticsSunshineDetail,
            "The streaming host was not checked.",
            "Sunshine responded to the host check.");
        RenderDiagnostic(
            snapshot.SunshineEndpoint,
            DiagnosticsEndpointDot,
            DiagnosticsEndpointStatus,
            DiagnosticsEndpointDetail,
            "The local Sunshine endpoint was not checked.",
            "The local Sunshine control endpoint responded.");
        RenderDiagnostic(
            snapshot.Firewall,
            DiagnosticsFirewallDot,
            DiagnosticsFirewallStatus,
            DiagnosticsFirewallDetail,
            "Windows Firewall was not checked.",
            "Firewall rules were inspected locally.");

        var gpuReady = snapshot.Gpus.Count > 0;
        DiagnosticsGpuDot.Background = gpuReady
            ? ResourceBrush("SuccessBrush")
            : ResourceBrush("WarningBrush");
        DiagnosticsGpuStatus.Text = gpuReady ? "Detected" : "Unavailable";
        DiagnosticsGpuStatus.Foreground = DiagnosticsGpuDot.Background;
        DiagnosticsGpuDetail.Text = gpuReady
            ? string.Join(Environment.NewLine, snapshot.Gpus.Select(gpu => $"{gpu.Name} · {gpu.Vendor}"))
            : "No display adapter information was returned.";
    }

    private void RenderTailscaleGuide(HostSnapshot snapshot)
    {
        if (NetworkSetupSection.ReadLocalValue(Expander.IsExpandedProperty) == DependencyProperty.UnsetValue)
            NetworkSetupSection.IsExpanded = snapshot.Tailscale.State == ComponentState.Missing;
        var installed = snapshot.Tailscale.State != ComponentState.Missing;
        var connected = snapshot.Tailscale.State == ComponentState.Ready;
        var stateBrush = StateBrush(snapshot.Tailscale.State);

        TailscaleGuideStatusDot.Background = stateBrush;
        TailscaleGuideStatusText.Foreground = stateBrush;
        TailscaleGuideStatusText.Text = UiText(connected
            ? "Connected"
            : installed
                ? "Sign-in required"
                : "Not installed");
        TailscaleGuideStatusDetail.Text = UiText(connected
            ? "The private network is ready. PiuLink will use this address for streaming."
            : installed
                ? "Tailscale is installed. Complete the account connection below."
                : "Install Tailscale to connect this PC to a private network.");
        TailscaleInstallStepStatus.Text = UiText(installed
            ? "Installed on this PC."
            : "Waiting for installation.");
        TailscaleInstallStepStatus.Foreground = installed
            ? ResourceBrush("SuccessBrush")
            : ResourceBrush("MutedBrush");
        TailscaleSignInButton.IsEnabled = installed && !connected && !_tailscaleSignInInProgress;
        TailscaleSignInButton.Content = UiText(connected
            ? "Connected"
            : _tailscaleSignInInProgress
                ? "Opening browser…"
                : "Start secure sign-in");
        if (!_tailscaleSignInInProgress)
        {
            TailscaleSignInStatusText.Text = UiText(connected
                ? "This PC is signed in and ready."
                : installed
                    ? "Ready to start Tailscale's secure authentication flow."
                    : "Install Tailscale first.");
            TailscaleSignInStatusText.Foreground = connected
                ? ResourceBrush("SuccessBrush")
                : ResourceBrush("MutedBrush");
        }
        TailscaleGuideAddressText.Text = UiText(snapshot.TailscaleIp ?? "No private IP");
        TailscaleGuideAddressText.Foreground = connected
            ? ResourceBrush("PrimaryBrush")
            : ResourceBrush("MutedBrush");
        SetTailscaleSettingsControlsBusy(_tailscaleSettingsBusy);
    }

    private void RenderDiagnostic(
        ComponentHealth? health,
        Border dot,
        TextBlock status,
        TextBlock detail,
        string missingDetail,
        string availableFallback)
    {
        if (health is null)
        {
            dot.Background = ResourceBrush("SubtleBrush");
            status.Text = "Not checked";
            status.Foreground = ResourceBrush("MutedBrush");
            detail.Text = missingDetail;
            return;
        }

        var brush = StateBrush(health.State);
        dot.Background = brush;
        status.Text = LocalizationService.Instance.TranslateLiteral(health.Summary);
        status.Foreground = brush;
        detail.Text = LocalizationService.Instance.TranslateLiteral(health.Detail ?? availableFallback);
    }

    private void RenderFailure(Exception exception)
    {
        var errorBrush = ResourceBrush("ErrorBrush");
        OverallStatus.Text = "Check failed";
        OverallStatus.Foreground = errorBrush;
        OverallStatusDot.Background = errorBrush;
        HeroStatusGlyph.Text = "!";
        HeroStatusTitle.Text = "Diagnostics could not finish";
        HeroStatusDescription.Text = "Try again. A local log was saved to help diagnose the problem.";
        ConnectionText.Text = exception.Message;
        DiagnosticsOverallStatus.Text = "Diagnostics failed";
        DiagnosticsOverallStatus.Foreground = errorBrush;
        DiagnosticsCheckedAt.Text = "Check failed";
        _trayNetworkStatus.Text = "Private network · Check failed";
        _traySunshineStatus.Text = "Streaming host · Check failed";
    }

    private (Forms.NotifyIcon Icon, Forms.ToolStripMenuItem Network, Forms.ToolStripMenuItem Sunshine,
        Forms.ToolStripMenuItem AutoStart, Forms.ToolStripMenuItem StartSilently) CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = TrayColor("PanelBrush"),
            ForeColor = TrayColor("TextBrush"),
            Font = new Drawing.Font("Segoe UI", 10F),
            Padding = new Forms.Padding(8),
            Renderer = new Forms.ToolStripProfessionalRenderer(new PiuLinkTrayColors())
        };
        var openItem = new Forms.ToolStripMenuItem("Open PiuLink Host") { Font = new Drawing.Font("Segoe UI Semibold", 10F) };
        var networkItem = new Forms.ToolStripMenuItem("Private network · Checking") { Enabled = false };
        var sunshineItem = new Forms.ToolStripMenuItem("Streaming host · Checking") { Enabled = false };
        var refreshItem = new Forms.ToolStripMenuItem("Refresh status");
        var recoverItem = new Forms.ToolStripMenuItem("Recover hidden windows");
        var logsItem = new Forms.ToolStripMenuItem("View logs");
        var sendItem = new Forms.ToolStripMenuItem("发送文件到手机…");
        sendItem.Click += (_, _) => Dispatcher.Invoke(() => SendFilesToPhone());
        var autoStartItem = new Forms.ToolStripMenuItem("Launch at sign-in") { CheckOnClick = true, Checked = StartupService.IsEnabled };
        var silentItem = new Forms.ToolStripMenuItem("Start silently in tray") { CheckOnClick = true, Checked = HostPreferences.StartSilently };
        var exitItem = new Forms.ToolStripMenuItem("Exit PiuLink");

        openItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        refreshItem.Click += (_, _) => Dispatcher.Invoke(async () => await RefreshAsync());
        recoverItem.Click += (_, _) => Dispatcher.Invoke(() => RecoverWindows_Click(this, new RoutedEventArgs()));
        logsItem.Click += (_, _) => Dispatcher.Invoke(() => ViewLogs_Click(this, new RoutedEventArgs()));
        autoStartItem.Click += (_, _) => Dispatcher.Invoke(() => UpdateStartupPreference(autoStartItem, silentItem));
        silentItem.Click += (_, _) => Dispatcher.Invoke(() => UpdateSilentStartupPreference(autoStartItem, silentItem));
        exitItem.Click += (_, _) => Dispatcher.Invoke(RequestExit);
        menu.Items.Add(openItem);
        menu.Items.Add(sendItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(networkItem);
        menu.Items.Add(sunshineItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(refreshItem);
        menu.Items.Add(recoverItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(silentItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        foreach (Forms.ToolStripItem item in menu.Items)
            item.Padding = new Forms.Padding(8, 6, 8, 6);

        var icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = CreatePiuLinkIcon(),
            Text = "PiuLink Host",
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return (icon, networkItem, sunshineItem, autoStartItem, silentItem);
    }

    internal void SendFilesToPhone(string[]? paths = null)
    {
        ShowFromTray();
        if (paths is null)
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择发送到手机的文件", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            paths = picker.FileNames;
        }
        var allPeers = _fileTransferPairings.Peers();
        var paired = PairedDisplaySecurity.ReadPairedFingerprints();
        var peers = paired is not null
            ? allPeers.Where(peer => peer.CertificateFingerprint is null || paired.Contains(peer.CertificateFingerprint)).ToList()
            : allPeers;
        if (peers.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "请先在手机分享一个文件到这台电脑，确认一次后即可双向传输。", "PiuLink 文件传输");
            return;
        }
        var dialog = new Window { Title = "发送到哪台手机？", Width = 440, Height = 320, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var dialogLifetime = new CancellationTokenSource();
        bool dialogClosed = false;
        bool sendInFlight = false;
        dialog.Closed += (_, _) =>
        {
            dialogClosed = true;
            dialogLifetime.Cancel();
            if (!sendInFlight) dialogLifetime.Dispose();
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"已选择 {paths.Length} 个文件", Margin = new Thickness(0, 0, 0, 16) });
        var progressBar = new System.Windows.Controls.ProgressBar { Height = 8, Maximum = 100 };
        var status = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var cancel = new System.Windows.Controls.Button { Content = "取消当前文件", IsEnabled = false };
        var destinations = new List<System.Windows.Controls.Button>();
        CancellationTokenSource? sending = null;
        cancel.Click += (_, _) => { cancel.IsEnabled = false; sending?.Cancel(); };
        panel.Children.Add(progressBar); panel.Children.Add(status); panel.Children.Add(cancel);
        var files = paths.Select(path => new System.Windows.Controls.TextBlock {
            Text = Path.GetFileName(path) + " · 等待发送", TextWrapping = TextWrapping.Wrap
        }).ToArray();
        foreach (var label in files) panel.Children.Add(label);
        foreach (var peer in peers) {
            var acknowledged = new HashSet<int>();
            var button = new System.Windows.Controls.Button { Content = peer.Name, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12) };
            destinations.Add(button);
            button.Click += async (_, _) => {
                sendInFlight = true;
                foreach (var choice in destinations) choice.IsEnabled = false;
                int nextFile = 0;
                for (int i = 0; i < files.Length; i++)
                    files[i].Text = Path.GetFileName(paths[i]) + (acknowledged.Contains(i) ? " · 手机已保存" : " · 等待发送");
                try {
                    for (; nextFile < paths.Length; nextFile++) {
                        if (acknowledged.Contains(nextFile)) continue;
                        dialogLifetime.Token.ThrowIfCancellationRequested();
                        var row = files[nextFile];
                        string path = paths[nextFile], file = Path.GetFileName(path);
                        string label = $"{nextFile + 1}/{paths.Length} · {file}";
                        using var current = CancellationTokenSource.CreateLinkedTokenSource(dialogLifetime.Token);
                        sending = current; cancel.IsEnabled = true; progressBar.Value = 0;
                        var clock = Stopwatch.StartNew();
                        row.Text = status.Text = label + " · 正在连接";
                        var progress = new Progress<(long Sent, long Total)>(value => {
                            if (dialogClosed || !ReferenceEquals(current, sending)) return;
                            progressBar.Value = value.Total == 0 ? 100 : 100.0 * value.Sent / value.Total;
                            row.Text = status.Text = $"{label} · {value.Sent / 1048576.0:F1}/{value.Total / 1048576.0:F1} MiB · " +
                                (value.Sent == value.Total ? "等待手机保存确认" : $"{value.Sent / 1048576.0 / Math.Max(0.1, clock.Elapsed.TotalSeconds):F1} MiB/s");
                        });
                        try {
                            await FileTransferSender.SendAsync(peer, path, current.Token, progress);
                            acknowledged.Add(nextFile);
                            row.Text = file + " · 手机已保存";
                        }
                        catch (OperationCanceledException) when (current.IsCancellationRequested && !dialogClosed) {
                            row.Text = file + " · 已取消";
                        }
                    }
                    status.Text = $"手机已确认保存 {acknowledged.Count}/{paths.Length} 个文件。";
                    if (acknowledged.Count == paths.Length) button.Content = "发送完成";
                    else status.Text += " 点击设备可重试未完成的文件。";
                } catch (OperationCanceledException) when (dialogClosed) {
                } catch (Exception error) {
                    if (nextFile < files.Length) files[nextFile].Text = Path.GetFileName(paths[nextFile]) + " · 未收到确认";
                    status.Text = $"第 {nextFile + 1} 个文件未确认。{error.Message} 点击设备可重试。";
                    await _log.WriteAsync($"File transfer failed to {peer.Name}: {error.Message}");
                } finally {
                    sendInFlight = false;
                    sending = null; cancel.IsEnabled = false;
                    if (dialogClosed) dialogLifetime.Dispose();
                    else foreach (var choice in destinations) choice.IsEnabled = !Equals(choice.Content, "发送完成");
                }
            };
            panel.Children.Add(button);
        }
        dialog.Content = new System.Windows.Controls.ScrollViewer { Content = panel }; dialog.Show();
    }

    private static void UpdateStartupPreference(Forms.ToolStripMenuItem autoStart, Forms.ToolStripMenuItem silent)
    {
        try { StartupService.SetEnabled(autoStart.Checked, silent.Checked); }
        catch
        {
            autoStart.Checked = StartupService.IsEnabled;
            System.Windows.MessageBox.Show("PiuLink could not update the Windows sign-in startup setting.", "PiuLink");
        }
    }

    private static void UpdateSilentStartupPreference(Forms.ToolStripMenuItem autoStart, Forms.ToolStripMenuItem silent)
    {
        HostPreferences.StartSilently = silent.Checked;
        if (autoStart.Checked) UpdateStartupPreference(autoStart, silent);
    }

    private void UpdateTrayStatus(HostSnapshot snapshot)
    {
        _trayNetworkStatus.Text = snapshot.Tailscale.State == ComponentState.Ready
            ? "Private network · Connected"
            : "Private network · Needs attention";
        _traySunshineStatus.Text = snapshot.Sunshine.State == ComponentState.Ready
            ? "Streaming host · Running"
            : snapshot.Sunshine.State == ComponentState.Missing
                ? "Streaming host · Not installed"
                : "Streaming host · Needs attention";
        _trayIcon.Text = snapshot.IsReady
            ? "PiuLink Host · Ready"
            : "PiuLink Host · Needs attention";
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray(showHint: false);
        }
        else
        {
            _lastVisibleWindowState = WindowState;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray(showHint: true);
    }

    private void HideToTray(bool showHint)
    {
        ShowInTaskbar = false;
        Hide();
        if (showHint && !_closeToTrayHintShown)
        {
            _closeToTrayHintShown = true;
            _trayIcon.ShowBalloonTip(
                2500,
                LocalizationService.Instance.TranslateLiteral("PiuLink is still running"),
                LocalizationService.Instance.TranslateLiteral(
                    "The Host was minimized to the notification area. Double-click the PiuLink icon to reopen it, or choose Exit PiuLink to stop it."),
                Forms.ToolTipIcon.Info);
        }
    }

    internal void HideOnStartup() => HideToTray(showHint: false);

    private async void InstallMicrophoneDevice_Click(object sender, RoutedEventArgs e) =>
        await ConfigureMicrophoneDeviceAsync(automatic: false);

    private void OpenMicrophoneSettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });

    private bool _microphoneSetupBusy;
    private async Task ConfigureMicrophoneDeviceAsync(bool automatic)
    {
        if (_microphoneSetupBusy) return;
        _microphoneSetupBusy = true;
        InstallMicrophoneDeviceButton.IsEnabled = false;
        string Tr(string message) => LocalizationService.Instance.TranslateLiteral(message);
        try {
            const string ready = "Ready. Streaming switches the default recording input to PiuLink and restores it on disconnect.";
            if (VirtualMicrophoneDevice.HasNativeDriver && await Task.Run(RemoteMicrophoneOutput.HasDedicatedPair)) {
                MicrophoneDeviceStatus.Text = Tr(ready);
                InstallMicrophoneDeviceButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (!VirtualMicrophoneDevice.HasPackage) {
                MicrophoneDeviceStatus.Text = Tr("Repair the Host installation to restore the microphone driver package.");
                return;
            }
            MicrophoneDeviceStatus.Text = Tr("The PiuLink recording device is not installed.");
            InstallMicrophoneDeviceButton.Visibility = Visibility.Visible;
            if (automatic) return;
            if (DisplayRecoveryJournal.Pending || (_sunshineCredential != null &&
                await _sunshineApi.GetActiveStreamCountAsync(_sunshineCredential.Username, _sunshineCredential.Secret, CancellationToken.None) != 0)) {
                MicrophoneDeviceStatus.Text = Tr("End the active stream before installing the microphone.");
                return;
            }
            var defaults = SessionMicrophoneRoute.CaptureInstallationDefaults();
            try {
                using var installer = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--install-virtual-microphone") {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                }) ?? throw new IOException("Windows could not start the microphone installer.");
                await installer.WaitForExitAsync();
                if (installer.ExitCode != 0 || !RemoteMicrophoneOutput.HasDedicatedPair())
                    throw new IOException(Tr("Windows did not enable both PiuLink audio endpoints."));
            } finally { SessionMicrophoneRoute.RestoreInstallationDefaults(defaults); }
            MicrophoneDeviceStatus.Text = Tr(ready);
            InstallMicrophoneDeviceButton.Visibility = Visibility.Collapsed;
        } catch (Exception error) {
            MicrophoneDeviceStatus.Text = error.Message;
            await _log.WriteAsync("Virtual microphone setup: " + error.Message);
        } finally {
            InstallMicrophoneDeviceButton.IsEnabled = true;
            _microphoneSetupBusy = false;
        }
    }

    internal void ShowFromTray()
    {
        bool wasHidden = !IsVisible;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastVisibleWindowState;
        Activate();
        if (wasHidden) _ = ConfigureMicrophoneDeviceAsync(automatic: true);
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private static Drawing.Icon CreatePiuLinkIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/PiuLink.ico", UriKind.Absolute));
            if (resource is not null)
            {
                using var embeddedIcon = new Drawing.Icon(resource.Stream);
                return (Drawing.Icon)embeddedIcon.Clone();
            }
        }
        catch (Exception)
        {
            // A tray icon is cosmetic and must never prevent the Host from starting.
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static Drawing.Color TrayColor(string key)
    {
        var brush = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.FindResource(key);
        return Drawing.Color.FromArgb(brush.Color.R, brush.Color.G, brush.Color.B);
    }

    private sealed class PiuLinkTrayColors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => TrayColor("PanelBrush");
        public override Drawing.Color MenuItemSelected => TrayColor("ControlBrush");
        public override Drawing.Color MenuItemBorder => TrayColor("BorderBrush");
        public override Drawing.Color ImageMarginGradientBegin => TrayColor("PanelBrush");
        public override Drawing.Color ImageMarginGradientMiddle => TrayColor("PanelBrush");
        public override Drawing.Color ImageMarginGradientEnd => TrayColor("PanelBrush");
        public override Drawing.Color SeparatorDark => TrayColor("DividerBrush");
        public override Drawing.Color SeparatorLight => TrayColor("PanelBrush");
        public override Drawing.Color ToolStripBorder => TrayColor("BorderBrush");
    }

    private System.Windows.Media.Brush StateBrush(ComponentState state) => state switch
    {
        ComponentState.Ready => ResourceBrush("SuccessBrush"),
        ComponentState.Installed => ResourceBrush("PrimaryBrush"),
        ComponentState.NeedsAttention => ResourceBrush("WarningBrush"),
        ComponentState.Missing => ResourceBrush("ErrorBrush"),
        _ => ResourceBrush("SubtleBrush")
    };

    private System.Windows.Media.Brush ResourceBrush(string key) =>
        (System.Windows.Media.Brush)FindResource(key);

    private void OverviewNav_Checked(object sender, RoutedEventArgs e)
    {
        // XAML checks Overview before its page has been constructed.
        if (OverviewPage != null) SwitchPage(HostPage.Overview);
    }

    private void NetworkNav_Checked(object sender, RoutedEventArgs e) => SwitchPage(HostPage.PrivateNetwork);

    private void StreamingNav_Checked(object sender, RoutedEventArgs e) => SwitchPage(HostPage.StreamingHost);

    private async void DisplaysNav_Checked(object sender, RoutedEventArgs e)
    {
        SwitchPage(HostPage.Displays);
        if (_displayState is null)
        {
            await LoadDisplaySettingsAsync();
        }
    }

    private void VirtualDisplayNav_Checked(object sender, RoutedEventArgs e)
    {
        SwitchPage(HostPage.VirtualDisplay);
        LoadVirtualDisplayStatus();
    }

    private void AdvancedSettingsNav_Checked(object sender, RoutedEventArgs e) =>
        SwitchPage(HostPage.AdvancedSettings);

    private async void WakeOnLanNav_Checked(object sender, RoutedEventArgs e)
    {
        SwitchPage(HostPage.WakeOnLan);
        await LoadWakeOnLanStatusAsync();
    }

    private void DiagnosticsNav_Checked(object sender, RoutedEventArgs e) => SwitchPage(HostPage.Diagnostics);

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageSelectorReady || LanguageSelector.SelectedValue is not string languageCode)
        {
            return;
        }

        HostPreferences.Language = languageCode;
        LocalizationService.Instance.SetLanguage(languageCode);
        UpdatePageHeader(_currentPage);
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsNavButton.IsChecked = true;
    }

    private void SwitchPage(HostPage target)
    {
        var previous = _currentPage;
        _currentPage = target;
        var (pageToShow, tab) = target switch
        {
            HostPage.PrivateNetwork => (NetworkPage, NetworkNavButton),
            HostPage.StreamingHost => (SunshinePage, StreamingNavButton),
            HostPage.Displays => (DisplaysPage, DisplaysNavButton),
            HostPage.VirtualDisplay => (VirtualDisplayPage, VirtualDisplayNavButton),
            HostPage.WakeOnLan => (WakeOnLanPage, WakeOnLanNavButton),
            HostPage.AdvancedSettings => (AdvancedSettingsPage, AdvancedSettingsNavButton),
            HostPage.Diagnostics => (DiagnosticsPage, DiagnosticsNavButton),
            _ => (OverviewPage, OverviewNavButton)
        };

        NavMarker.Track(tab);
        PageArtwork.Symbol = (HostSymbol)tab.Tag;
        UpdatePageHeader(target);

        if (pageToShow.Visibility == Visibility.Visible)
        {
            return;
        }

        foreach (var page in new UIElement[]
                 {
                     OverviewPage,
                     NetworkPage,
                     SunshinePage,
                      DisplaysPage,
                      VirtualDisplayPage,
                      WakeOnLanPage,
                      AdvancedSettingsPage,
                      DiagnosticsPage
                 })
        {
            StopPageMotion(page);
            page.Visibility = Visibility.Collapsed;
        }

        pageToShow.Visibility = Visibility.Visible;
        AnimatePageIn(pageToShow, target > previous ? 1 : -1);
    }

    private void ConfigureArtwork()
    {
        NavMarker.Track(OverviewNavButton);
        BrandTile.MouseEnter += (_, _) => BrandArtwork.Replay();
        HeaderBand.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 655;
            Grid.SetColumn(PageArtwork, compact ? 0 : 2);
            PageArtwork.Width = compact ? 31 : 176;
            PageArtwork.Height = compact ? 43 : 68;
            PageArtwork.Margin = compact ? new Thickness(0, 0, 12, 0) : new Thickness(4, 0, 12, 0);
        };
        foreach (var button in new[] { RefreshButton, TailscaleSignInButton, SaveTailscaleSettingsButton,
                     SunshineRefreshButton, ApplyDisplaySettingsButton, RefreshDisplaysButton, CreateVirtualDisplayButton,
                     DisableVirtualDisplayButton, LoadSunshineConfigurationButton, SaveSunshineConfigurationButton, ExportButton })
            button.Click += (_, _) => PageArtwork.Replay();
        CopyButton.Click += (_, _) => AddressIcon.Replay();
        InviteButton.Click += (_, _) => InvitationIcon.Replay();
        BrowserAccessButton.Click += (_, _) => BrowserIcon.Replay();
    }

    private void UpdatePageHeader(HostPage target)
    {
        var (titleKey, subtitleKey) = target switch
        {
            HostPage.Overview => ("Nav.Overview", "Page.Overview.Subtitle"),
            HostPage.PrivateNetwork => ("Nav.PrivateNetwork", "Page.PrivateNetwork.Subtitle"),
            HostPage.StreamingHost => ("Nav.StreamingHost", "Page.StreamingHost.Subtitle"),
            HostPage.Displays => ("Nav.Displays", "Page.Displays.Subtitle"),
            HostPage.VirtualDisplay => ("Nav.VirtualDisplay", "Page.VirtualDisplay.Subtitle"),
            HostPage.WakeOnLan => ("Nav.WakeOnLan", "Page.WakeOnLan.Subtitle"),
            HostPage.AdvancedSettings => ("Nav.AdvancedSettings", "Page.AdvancedSettings.Subtitle"),
            HostPage.Diagnostics => ("Nav.Diagnostics", "Page.Diagnostics.Subtitle"),
            _ => ("Nav.Overview", "Page.Overview.Subtitle")
        };

        PageTitle.Text = LocalizationService.Instance.Translate(titleKey);
        PageSubtitle.Text = LocalizationService.Instance.Translate(subtitleKey);
        PageSubtitle.Visibility = target == HostPage.Overview ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadWakeOnLanStatusAsync()
    {
        WakeOnLanStatusTitle.Text = UiText("Checking Wake-on-LAN…");
        WakeOnLanStatusDetail.Text = UiText("Inspecting active network adapters and Windows wake permissions.");
        WakeOnLanAdapterList.Children.Clear();

        WakeOnLanStatus status;
        try
        {
            status = await _wakeOnLan.InspectAsync();
        }
        catch (Exception exception)
        {
            WakeOnLanStatusTitle.Text = UiText("Wake-on-LAN inspection failed");
            WakeOnLanStatusDetail.Text = UiText(exception.Message);
            await _log.WriteAsync($"Wake-on-LAN inspection failed: {exception.GetType().Name}");
            return;
        }

        if (!status.HasCandidate)
        {
            WakeOnLanStatusTitle.Text = UiText("No active Ethernet or Wi-Fi adapter found");
            WakeOnLanStatusDetail.Text = UiText(status.Error ??
                "Connect this PC to the network, then refresh the Wake-on-LAN page.");
            AddWakeOnLanEmptyState();
            return;
        }

        WakeOnLanStatusTitle.Text = UiText(status.HasArmedAdapter
            ? "Windows allows this PC to wake"
            : "Wake-on-LAN needs adapter setup");
        WakeOnLanStatusDetail.Text = UiText(status.HasArmedAdapter
            ? "At least one active adapter is armed for wake events. Copy its MAC address for the client."
            : status.PowerQuerySucceeded
                ? "No active adapter is currently armed. Open Device Manager and enable Magic Packet wake."
                : status.Error ?? "PiuLink could not read Windows wake permissions.");

        foreach (var adapter in status.Adapters)
        {
            AddWakeOnLanAdapter(adapter);
        }
    }

    private void AddWakeOnLanAdapter(WakeOnLanAdapter adapter)
    {
        var border = new Border
        {
            BorderBrush = ResourceBrush("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var details = new StackPanel();
        details.Children.Add(new TextBlock
        {
            Text = adapter.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        details.Children.Add(new TextBlock
        {
            Text = $"{adapter.MacAddress}  ·  " +
                   (adapter.Ipv4Addresses.Count == 0
                       ? UiText("No IPv4 address")
                       : string.Join(", ", adapter.Ipv4Addresses)),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            Foreground = ResourceBrush("MutedBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        });
        details.Children.Add(new TextBlock
        {
            Text = UiText(adapter.IsWakeArmed ? "Wake armed" : "Wake permission not detected"),
            Foreground = adapter.IsWakeArmed
                ? ResourceBrush("SuccessBrush")
                : ResourceBrush("WarningBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var copyButton = new System.Windows.Controls.Button
        {
            Content = UiText("Copy MAC"),
            Tag = adapter.MacAddress,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        copyButton.Click += CopyWakeMac_Click;
        Grid.SetColumn(copyButton, 1);
        grid.Children.Add(details);
        grid.Children.Add(copyButton);
        border.Child = grid;
        WakeOnLanAdapterList.Children.Add(border);
    }

    private void AddWakeOnLanEmptyState()
    {
        WakeOnLanAdapterList.Children.Add(new TextBlock
        {
            Text = UiText("No suitable active adapter is available."),
            Foreground = ResourceBrush("MutedBrush"),
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 4)
        });
    }

    private async void CopyWakeMac_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string macAddress } && macAddress != "Unavailable")
        {
            var result = await DesktopClipboard.CopyAsync(this, macAddress);
            if (result != CopyResult.Canceled)
                ((System.Windows.Controls.Button)sender).Content = LocalizationService.Instance.TranslateLiteral(
                    result == CopyResult.Copied ? "Copied" : "Copy failed. Retry");
        }
    }

    private async void RefreshWakeOnLan_Click(object sender, RoutedEventArgs e)
    {
        PageArtwork.Replay();
        await LoadWakeOnLanStatusAsync();
    }

    private void OpenWakeDeviceManager_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });

    private void OpenWakePowerOptions_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("powercfg.cpl") { UseShellExecute = true });

    private static void StopPageMotion(UIElement page)
    {
        page.BeginAnimation(OpacityProperty, null);
        if (page.RenderTransform is TranslateTransform offset)
            offset.BeginAnimation(TranslateTransform.YProperty, null);
        page.RenderTransform = Transform.Identity;
    }

    private static void AnimatePageIn(UIElement page, int direction)
    {
        if (!HostMotion.Enabled) return;
        var offset = new TranslateTransform();
        page.RenderTransform = offset;
        var arrival = HostMotion.Travel(8 * direction, 0, 185);
        arrival.Completed += (_, _) => { if (page.RenderTransform == offset) StopPageMotion(page); };
        offset.BeginAnimation(TranslateTransform.YProperty, arrival);
        page.BeginAnimation(OpacityProperty, HostMotion.Travel(.88, 1, 145));
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        _copyFeedbackCancellation?.Cancel();
        CopyButton.Content = LocalizationService.Instance.TranslateLiteral("Copy connection info");
        using var feedback = new CancellationTokenSource();
        _copyFeedbackCancellation = feedback;
        try
        {
            var result = await DesktopClipboard.CopyAsync(this, _snapshot.ConnectionSummary, feedback.Token);
            if (result == CopyResult.Canceled) return;
            CopyButton.Content = LocalizationService.Instance.TranslateLiteral(
                result == CopyResult.Copied ? "Copied" : "Copy failed. Retry");
            await Task.Delay(result == CopyResult.Copied ? 1400 : 3500, feedback.Token);
            CopyButton.Content = LocalizationService.Instance.TranslateLiteral("Copy connection info");
        }
        catch (OperationCanceledException)
        {
            // A newer press owns the completion feedback.
        }
        finally { if (_copyFeedbackCancellation == feedback) _copyFeedbackCancellation = null; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"piulink-host-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Text report (*.txt)|*.txt",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, HostDiagnosticsService.FormatReport(_snapshot));
        _exportFeedbackCancellation?.Cancel();
        _exportFeedbackCancellation = new CancellationTokenSource();
        ExportButton.Content = "Report saved";

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1600), _exportFeedbackCancellation.Token);
            ExportButton.Content = "Export report";
        }
        catch (OperationCanceledException)
        {
            // A newer export owns the completion feedback.
        }
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        new LogViewerWindow(_log) { Owner = this }.ShowDialog();
    }

    private async Task InspectSunshinePayloadAsync()
    {
        SunshinePayloadStatusText.Text = "Verifying bundled installer…";
        SunshinePayloadStatusText.Foreground = ResourceBrush("MutedBrush");

        try
        {
            _sunshinePayloadStatus = await _sunshineInstaller.InspectAsync();
            SunshinePayloadStatusText.Text = _sunshinePayloadStatus.Message;
            SunshinePayloadStatusText.Foreground = _sunshinePayloadStatus.State switch
            {
                BundledInstallerState.Ready => ResourceBrush("SuccessBrush"),
                BundledInstallerState.Invalid => ResourceBrush("ErrorBrush"),
                _ => ResourceBrush("WarningBrush")
            };

            if (_sunshinePayloadStatus.State == BundledInstallerState.Invalid)
            {
                await _log.WriteAsync($"Bundled Sunshine payload rejected: {_sunshinePayloadStatus.Message}");
            }
        }
        catch (Exception exception)
        {
            _sunshinePayloadStatus = new BundledInstallerStatus(
                BundledInstallerState.Invalid,
                "PiuLink could not verify the bundled Sunshine component.");
            SunshinePayloadStatusText.Text = _sunshinePayloadStatus.Message;
            SunshinePayloadStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Bundled Sunshine inspection failed: {exception.GetType().Name}");
        }
        finally
        {
            UpdateSunshineInstallButton();
        }
    }

    private void SunshineInstallConsent_Changed(object sender, RoutedEventArgs e) =>
        UpdateSunshineInstallButton();

    private void UpdateSunshineInstallButton()
    {
        var sunshineMissing = _snapshot?.Sunshine.State == ComponentState.Missing;
        SunshineInstallButton.IsEnabled = sunshineMissing &&
                                           SunshineInstallConsentCheckBox.IsChecked == true &&
                                           _sunshinePayloadStatus?.State == BundledInstallerState.Ready;
    }

    private async void InstallSunshine_Click(object sender, RoutedEventArgs e)
    {
        if (SunshineInstallConsentCheckBox.IsChecked != true)
        {
            SunshinePayloadStatusText.Text = "Read and accept the Sunshine GPLv3 component notice first.";
            SunshinePayloadStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        SunshineInstallButton.IsEnabled = false;
        SunshineInstallButton.Content = "Waiting for Windows…";
        SunshineInstallConsentCheckBox.IsEnabled = false;
        SunshinePayloadStatusText.Text = "Approve the Windows administrator prompt to install Sunshine.";
        SunshinePayloadStatusText.Foreground = ResourceBrush("PrimaryBrush");

        try
        {
            var result = await _sunshineInstaller.InstallAsync();
            SunshinePayloadStatusText.Text = result.Message;
            SunshinePayloadStatusText.Foreground = result.Succeeded
                ? ResourceBrush("SuccessBrush")
                : result.Cancelled
                    ? ResourceBrush("WarningBrush")
                    : ResourceBrush("ErrorBrush");
            await _log.WriteAsync(
                $"Bundled Sunshine install finished. Success={result.Succeeded}; Cancelled={result.Cancelled}; ExitCode={result.ExitCode}");

            if (result.Succeeded)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200));
                await RefreshAsync();
            }
        }
        catch (Exception exception)
        {
            SunshinePayloadStatusText.Text = "Sunshine installation failed. No unverified fallback was executed.";
            SunshinePayloadStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Bundled Sunshine install failed: {exception.GetType().Name}");
        }
        finally
        {
            SunshineInstallButton.Content = "Install Sunshine";
            SunshineInstallConsentCheckBox.IsEnabled = true;
            UpdateSunshineInstallButton();
        }
    }

    private async void OpenSunshineLicense_Click(object sender, RoutedEventArgs e)
    {
        var licensePath = await _sunshineInstaller.GetLicensePathAsync();
        if (licensePath is null || !File.Exists(licensePath))
        {
            SunshinePayloadStatusText.Text = "The bundled GPLv3 license file is missing.";
            SunshinePayloadStatusText.Foreground = ResourceBrush("ErrorBrush");
            return;
        }

        Process.Start(new ProcessStartInfo(licensePath) { UseShellExecute = true });
    }

    private async void OpenSunshineSource_Click(object sender, RoutedEventArgs e)
    {
        if (_sunshinePayloadStatus?.Manifest is null)
        {
            await InspectSunshinePayloadAsync();
        }

        var sourceUri = _sunshinePayloadStatus?.Manifest?.SourceUri;
        if (sourceUri is not null)
        {
            Process.Start(new ProcessStartInfo(sourceUri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private async void ConnectSunshine_Click(object sender, RoutedEventArgs e)
    {
        // Repair the engine with the durable Windows credential. Reinstalling or repairing
        // PiuLink must not rotate the device's Sunshine login identity.
        if (!await ProvisionSunshineControlAsync(automatic: false)) return;

        await LoadSunshineOverviewAsync(repairIfNeeded: false);
        if (_sunshineAuthenticated)
        {
            await LoadDisplaySettingsAsync();
            await LoadSunshineConfigurationAsync();
        }
    }

    private async Task<bool> ProvisionSunshineControlAsync(bool automatic)
    {
        SetSunshineControlsBusy(isBusy: true);
        SunshineConnectButton.Content = "Connecting…";
        SunshineControlStatusText.Text = automatic
            ? "Finishing the local streaming connection…"
            : "Approve the administrator prompt once to link Sunshine with PiuLink.";
        SunshineControlStatusText.Foreground = ResourceBrush("PrimaryBrush");
        try
        {
            var result = await _sunshineManagedIdentity.ProvisionAsync();
            SunshineControlStatusText.Text = result.Message;
            SunshineControlStatusText.Foreground = result.Succeeded
                ? ResourceBrush("SuccessBrush")
                : result.Cancelled ? ResourceBrush("WarningBrush") : ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"PiuLink-managed Sunshine identity setup finished. Automatic={automatic}; Success={result.Succeeded}; Cancelled={result.Cancelled}");
            if (!result.Succeeded || result.Credential is null) return false;
            _sunshineCredential = result.Credential;
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            await RefreshAsync();
            return true;
        }
        catch (Exception exception)
        {
            SunshineControlStatusText.Text = "PiuLink could not initialize secure Sunshine control.";
            SunshineControlStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"PiuLink-managed Sunshine identity setup failed: {exception.GetType().Name}");
            return false;
        }
        finally
        {
            SetSunshineControlsBusy(isBusy: false);
        }
    }

    private async void RefreshSunshineControl_Click(object sender, RoutedEventArgs e)
    {
        await LoadSunshineOverviewAsync();
        if (_sunshineAuthenticated)
        {
            await LoadSunshineConfigurationAsync();
        }
    }

    private async Task LoadSunshineOverviewAsync(bool repairIfNeeded = true)
    {
        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null)
        {
            _sunshineAuthenticated = false;
            SunshineControlStatusText.Text = "Set up secure control once. PiuLink will manage the device credential for you.";
            SunshineControlStatusText.Foreground = ResourceBrush("WarningBrush");
            PairingStatusText.Text = "Set up PiuLink control first.";
            SetSunshineControlsBusy(isBusy: false);
            return;
        }

        _sunshineCredential = credential;

        SetSunshineControlsBusy(isBusy: true);
        SunshineControlStatusText.Text = "Connecting to Sunshine on this PC…";
        SunshineControlStatusText.Foreground = ResourceBrush("MutedBrush");

        try
        {
            var overview = await GetSunshineOverviewWithRetryAsync(credential);
            _sunshineAuthenticated = true;
            RenderSunshineOverview(overview);
            if (!_virtualApplicationsRegistered && !overview.HasActiveStreams) {
                try {
                    await _sunshineApi.RemoveManagedVirtualApplicationsAsync(credential.Username, credential.Secret);
                    _virtualApplicationsRegistered = true;
                } catch (Exception error) { await _log.WriteAsync("Retired virtual app cleanup deferred: " + DescribeExceptionForLog(error)); }
            }
            SunshineControlStatusText.Text = "Connected locally. Sunshine is now managed inside PiuLink.";
            SunshineControlStatusText.Foreground = ResourceBrush("SuccessBrush");
            PairingStatusText.Text = "Ready for a four-digit PIN from a PiuLink client.";
            PairingStatusText.Foreground = ResourceBrush("MutedBrush");
            await _log.WriteAsync("Connected to the local Sunshine API from the native host page.");
        }
        catch (Exception exception)
        {
            _sunshineAuthenticated = false;
            _sunshineOverview = null;
            _sunshineConfigurationLoaded = false;
            SunshineAppsCountText.Text = "—";
            SunshineClientsCountText.Text = "—";
            SunshineSessionStateText.Text = "—";
            SunshineSessionStatusText.Text = "PiuLink could not inspect the current Sunshine session.";
            EndSunshineSessionButton.Visibility = Visibility.Collapsed;
            SunshineControlStatusText.Text = DescribeSunshineFailure(exception);
            SunshineControlStatusText.Foreground = ResourceBrush("ErrorBrush");
            PairingStatusText.Text = "Repair PiuLink secure control first.";
            PairingStatusText.Foreground = ResourceBrush("MutedBrush");
            await _log.WriteAsync($"Local Sunshine API connection failed: {DescribeExceptionForLog(exception)}");

            // The engine and Credential Manager are separate stores.  If an engine
            // reinstall/reset leaves only the Windows credential behind, Sunshine
            // redirects us to its HTML login page and JSON parsing fails.  Repair the
            // identity once and retry without requiring the user to understand either
            // store.  The guard prevents an elevation cancellation or real engine
            // failure from becoming a retry loop.
            if (repairIfNeeded &&
                _snapshot?.Sunshine.State != ComponentState.Missing &&
                await ProvisionSunshineControlAsync(automatic: true))
            {
                await LoadSunshineOverviewAsync(repairIfNeeded: false);
            }
        }
        finally
        {
            SetSunshineControlsBusy(isBusy: false);
        }
    }

    private async void RefreshDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteDisplaySettingsBusy) return;
        _displayInventoryRefreshAttempted = false;
        await LoadDisplaySettingsAsync();
    }

    private void StreamingDisplay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_displaySettingsBusy &&
            StreamingDisplayComboBox.SelectedItem is DisplayChoice choice)
        {
            if (choice.DeviceId is null)
            {
                ExclusiveDisplayCheckBox.IsChecked = false;
            }
            else if (!string.Equals(
                         choice.DeviceId,
                         _displayState?.SelectedDeviceId,
                         StringComparison.OrdinalIgnoreCase))
            {
                // A newly selected target means "use this display" in PiuLink. Users can still
                // explicitly turn exclusive mode off before the short autosave delay finishes.
                ExclusiveDisplayCheckBox.IsChecked = true;
            }

            if (_currentPage == HostPage.Displays) PageArtwork.Replay();
            ScheduleDisplaySettingsAutoSave();
        }

        UpdateDisplayControls();
    }

    private void ExclusiveDisplay_Changed(object sender, RoutedEventArgs e)
    {
        ScheduleDisplaySettingsAutoSave();
        UpdateDisplayControls();
    }

    private void MoveWindowsPreference_Changed(object sender, RoutedEventArgs e)
    {
        HostPreferences.MoveWindowsToVirtualDisplay =
            MoveWindowsToVirtualDisplayCheckBox.IsChecked == true;
        UpdateDisplayControls();
    }

    private void RecoverWindowsPreference_Changed(object sender, RoutedEventArgs e) =>
        HostPreferences.RecoverWindowsWhenDisplayDisappears =
            RecoverWindowsWhenDisplayDisappearsCheckBox.IsChecked == true;

    private void SmartDisplayPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (_displaySettingsBusy)
        {
            return;
        }

        HostPreferences.SmartDisplayEnabled = SmartDisplayCheckBox.IsChecked == true;
        ScheduleDisplaySettingsAutoSave();
        UpdateDisplayControls();
    }

    private void SessionGuardianPreference_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = SessionGuardianCheckBox.IsChecked == true;
        HostPreferences.SessionGuardianEnabled = enabled;
        if (!enabled)
        {
            _sessionGuardian.Reset();
            SessionGuardianStatusText.Text = "Automatic repair is off. PiuLink will still show a manual recovery button.";
            SessionGuardianStatusText.Foreground = ResourceBrush("MutedBrush");
        }
        else
        {
            SessionGuardianStatusText.Text = "Watching for a disconnected client that leaves its app behind.";
            SessionGuardianStatusText.Foreground = ResourceBrush("SuccessBrush");
        }
    }

    private async void SessionGuardianTimer_Tick(object? sender, EventArgs e)
    {
        if (_sessionGuardianBusy || _remoteDisplaySettingsBusy || _displaySettingsBusy ||
            _sunshineConfigurationBusy ||
            SessionGuardianCheckBox.IsChecked != true ||
            !_sunshineAuthenticated)
        {
            return;
        }

        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null)
        {
            return;
        }

        _sessionGuardianBusy = true;
        try
        {
            var overview = await _sunshineApi.GetOverviewAsync(
                credential.Username,
                credential.Secret);
            if (_remoteDisplaySettingsBusy || _sunshineConfigurationBusy) return;
            RenderSunshineOverview(overview);
            var observation = _sessionGuardian.Observe(overview, DateTimeOffset.UtcNow);
            switch (observation.State)
            {
                case SunshineSessionGuardState.Ready:
                    SessionGuardianStatusText.Text = "Ready. Session Guardian is watching for stale sessions.";
                    SessionGuardianStatusText.Foreground = ResourceBrush("SuccessBrush");
                    break;

                case SunshineSessionGuardState.ActiveStream:
                    SessionGuardianStatusText.Text = "An active client is protected. Automatic repair will never interrupt it.";
                    SessionGuardianStatusText.Foreground = ResourceBrush("PrimaryBrush");
                    break;

                case SunshineSessionGuardState.GracePeriod:
                    var remaining = Math.Max(
                        1,
                        (int)Math.Ceiling((_sessionGuardian.GracePeriod - observation.RetainedFor).TotalSeconds));
                    SessionGuardianStatusText.Text = $"Client disconnected. Waiting {remaining}s before releasing the retained app…";
                    SessionGuardianStatusText.Foreground = ResourceBrush("WarningBrush");
                    break;

                case SunshineSessionGuardState.RecoveryRequired:
                    if (_remoteDisplaySettingsBusy || _sunshineConfigurationBusy) return;
                    await RecoverRetainedSessionAsync(credential, observation.ApplicationId);
                    break;
            }
        }
        catch (Exception exception)
        {
            SessionGuardianStatusText.Text = "Session Guardian will retry when Sunshine becomes available.";
            SessionGuardianStatusText.Foreground = ResourceBrush("WarningBrush");
            await _log.WriteAsync($"Session Guardian check failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _sessionGuardianBusy = false;
        }
    }

    private static bool IsDisplayLeaseOrRecoveryBusy() =>
        DisplayRecoveryJournal.Read() is { Phase: "Restoring" or "Switching" or "AwaitingEngine" } ||
        (DisplayRecoveryJournal.Read() is { Phase: "Preparing" or "Ready" } activeLease &&
         activeLease.ExpiresAt > DateTimeOffset.UtcNow);

    private async Task RecoverRetainedSessionAsync(
        SunshineDeviceCredential credential,
        int retainedApplicationId)
    {
        SessionGuardianStatusText.Text = "Releasing the retained app safely…";
        SessionGuardianStatusText.Foreground = ResourceBrush("PrimaryBrush");

        var fresh = await _sunshineApi.GetOverviewAsync(credential.Username, credential.Secret);
        if (_remoteDisplaySettingsBusy || _sunshineConfigurationBusy || IsDisplayLeaseOrRecoveryBusy())
        {
            return;
        }
        if (!fresh.HasRetainedApplication || fresh.CurrentApplicationId != retainedApplicationId)
        {
            _sessionGuardian.Reset();
            RenderSunshineOverview(fresh);
            return;
        }

        Exception? closeFailure = null;
        try
        {
            await _sunshineApi.CloseCurrentApplicationAsync(credential.Username, credential.Secret);
        }
        catch (Exception exception)
        {
            closeFailure = exception;
            await _log.WriteAsync(
                $"Session Guardian close request failed; verifying before escalation: {DescribeExceptionForLog(exception)}");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(700));
        if (_remoteDisplaySettingsBusy || _sunshineConfigurationBusy || IsDisplayLeaseOrRecoveryBusy())
        {
            return;
        }
        var afterClose = await GetSunshineOverviewWithRetryAsync(credential);
        var restartEscalation = false;
        if (afterClose.HasRetainedApplication &&
            !afterClose.HasActiveStreams &&
            afterClose.CurrentApplicationId == retainedApplicationId)
        {
            if (_remoteDisplaySettingsBusy || _sunshineConfigurationBusy || IsDisplayLeaseOrRecoveryBusy())
            {
                return;
            }
            SessionGuardianStatusText.Text = "The app did not release. Restarting secure streaming control…";
            restartEscalation = true;
            await _sunshineApi.RestartAsync(credential.Username, credential.Secret);
            afterClose = await GetSunshineOverviewWithRetryAsync(credential);
        }

        if (afterClose.HasRetainedApplication && !afterClose.HasActiveStreams)
        {
            throw new InvalidOperationException(
                "Sunshine kept the previous application after safe recovery.",
                closeFailure);
        }

        _sessionGuardian.Reset();
        RenderSunshineOverview(afterClose);
        SessionGuardianStatusText.Text = "Recovered automatically. Sunshine is ready for a new connection.";
        SessionGuardianStatusText.Foreground = ResourceBrush("SuccessBrush");
        await _log.WriteAsync(
            $"Session Guardian recovered retained application {retainedApplicationId}. RestartEscalation={restartEscalation}");
    }

    private bool UsesExclusiveVirtualDesktop => _displayState is { ExclusiveDisplay: true } state &&
        state.Displays.Any(display => display.IsVirtual &&
            string.Equals(display.DeviceId, state.SelectedDeviceId, StringComparison.OrdinalIgnoreCase));

    private string? ExclusiveVirtualDesktopDisplayName => _displayState is { ExclusiveDisplay: true } state
        ? state.Displays.FirstOrDefault(display => display.IsVirtual &&
              string.Equals(display.DeviceId, state.SelectedDeviceId, StringComparison.OrdinalIgnoreCase))?.DisplayName
        : null;

    private string? ExclusiveVirtualDesktopFriendlyName => _displayState is { ExclusiveDisplay: true } state
        ? state.Displays.FirstOrDefault(display => display.IsVirtual &&
              string.Equals(display.DeviceId, state.SelectedDeviceId, StringComparison.OrdinalIgnoreCase))?.FriendlyName
        : null;

    private void SessionDisplaySettingsChanging(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (UsesExclusiveVirtualDesktop || WindowPlacementService.HasSessionTransition)
                WindowPlacementService.BeginSessionDesktopTransition();
        });
    }

    private Task LogWindowResultAsync(string eventId, string message, int failed) =>
        failed > 0
            ? _log.ErrorAsync(eventId, "display-window", message)
            : _log.InfoAsync(eventId, "display-window", message);

    private async void WindowRecoveryTimer_Tick(object? sender, EventArgs e)
    {
        bool? observedActiveStreams = null;
        if (_physicalDisplayRecoveryBusy || _remoteDisplaySettingsBusy) return;
        if (!_displaySettingsBusy && !_virtualDisplayBusy && _displayState is not null)
        {
            _physicalDisplayRecoveryBusy = true;
            try
            {
                var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
                if (credential is null) return;
                // A stale cached overview must never authorize changing the active stream's monitors.
                var overview = await GetSunshineOverviewWithRetryAsync(credential);
                observedActiveStreams = overview.HasActiveStreams;
                if (!_virtualApplicationsRegistered && !overview.HasActiveStreams) {
                    await _sunshineApi.RemoveManagedVirtualApplicationsAsync(credential.Username, credential.Secret);
                    _virtualApplicationsRegistered = true;
                }
                if (_displaySettingsBusy || _virtualDisplayBusy || _remoteDisplaySettingsBusy) return;
                var current = DisplayTopologyService.ReadDesktopState();
                WindowPlacementService.RestoreLocalApplications(!overview.HasActiveStreams && current.PhysicalOnly, current.Layout, Environment.TickCount64);

            }
            catch (Exception exception)
            {
                await _log.WriteAsync($"Physical display recovery deferred: {DescribeExceptionForLog(exception)}");
                return;
            }
            finally { _physicalDisplayRecoveryBusy = false; }
        }
        try
        {
            if (_displaySettingsBusy || _virtualDisplayBusy || _remoteDisplaySettingsBusy) return;
            // A failed Sunshine inventory read must not discard the session snapshot.
            if (_displayState is null) return;
            if ((observedActiveStreams ?? _sunshineOverview?.HasActiveStreams) == true)
            {
                if (_gatherWindowsTarget is not null && !_gatherWindowsApplied)
                {
                    var desktop = DisplayTopologyService.ReadDesktopState();
                    if (desktop.PhysicalOnly)
                    {
                        WindowPlacementService.CaptureSessionDesktop(desktop.Layout);
                        WindowPlacementService.BeginSessionDesktopTransition();
                        var gathered = WindowPlacementService.MoveApplicationWindows(_gatherWindowsTarget);
                        _gatherWindowsApplied = true;
                        GatherWindowsStatus.Text = gathered.Failed > 0
                            ? $"串流正常；已移动 {gathered.Moved} 个窗口，{gathered.Failed} 个窗口未能移动（可能属于管理员程序）。"
                            : $"已将 {gathered.Moved} 个窗口集中到所选屏幕。";
                        RetryGatherWindowsButton.Visibility = gathered.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
                        await LogWindowResultAsync("window.gather", $"Physical desktop windows gathered: Moved={gathered.Moved}; Failed={gathered.Failed}", gathered.Failed);
                    }
                }
                if (UsesExclusiveVirtualDesktop || WindowPlacementService.HasSessionTransition)
                    WindowPlacementService.BeginSessionDesktopTransition();
                if (UsesExclusiveVirtualDesktop)
                {
                    var activeDesktop = DisplayTopologyService.ReadDesktopState();
                    var virtualDisplayName = ExclusiveVirtualDesktopDisplayName;
                    var virtualFriendlyName = ExclusiveVirtualDesktopFriendlyName;
                    var activeVirtualDisplayName = DisplayTopologyService.FindActiveVirtualDisplayName(
                        virtualDisplayName ?? string.Empty, virtualFriendlyName);
                    if (!activeDesktop.PhysicalOnly && !string.IsNullOrWhiteSpace(activeVirtualDisplayName))
                    {
                        // Virtual streaming is a true one-desktop session: leave only the VDD
                        // active. Windows consequently moves shell/app windows onto it instead
                        // of leaving Explorer behind on a still-active physical monitor.
                        await TryApplyPendingDisplayScaleAsync(activeVirtualDisplayName);
                        var adapted = WindowPlacementService.AdaptToVirtualDesktop(
                            activeDesktop.Layout, activeVirtualDisplayName, Environment.TickCount64);
                        if (adapted.Moved > 0 || adapted.Failed > 0)
                        {
                            GatherWindowsStatus.Text = adapted.Failed > 0
                                ? $"虚拟屏幕已移动 {adapted.Moved} 个窗口；{adapted.Failed} 个窗口未能移动（可能属于管理员程序）。"
                                : $"已将 {adapted.Moved} 个窗口集中到虚拟显示器。";
                            RetryGatherWindowsButton.Visibility = adapted.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
                            await LogWindowResultAsync("window.virtual_size", $"Virtual desktop window sizing: Moved={adapted.Moved}; Failed={adapted.Failed}", adapted.Failed);
                        }
                    }
                    else WindowPlacementService.ResetStreamLayout();
                }
                else WindowPlacementService.ResetStreamLayout();
                return;
            }
            {
                // Recovery belongs to the previous session, not the newly selected mode.
                if (_gatherWindowsApplied) {
                    _gatherWindowsTarget = null; _gatherWindowsApplied = false;
                    GatherWindowsStatus.Text = "串流已结束，正在恢复窗口布局。";
                    RetryGatherWindowsButton.Visibility = Visibility.Collapsed;
                }
                // Also keep an idle baseline when a client skips an unchanged routing request.
                var desktop = DisplayTopologyService.ReadDesktopState();
                var restored = WindowPlacementService.ObserveSessionDesktop(desktop.PhysicalOnly, desktop.Layout);
                // Do not move windows while Sunshine is changing resolution or disabling monitors.
                if (!desktop.PhysicalOnly || WindowPlacementService.SessionRestorePending) return;
                if (restored.Moved > 0 || restored.Failed > 0)
                {
                    await LogWindowResultAsync("window.session_restore", $"Session window restore completed. Restored={restored.Moved}; Failed={restored.Failed}", restored.Failed);
                    // SetWindowPlacement is asynchronous. Do not race it with a second move.
                    return;
                }
            }
            // This preference controls unsolicited off-screen recovery only. Explicit
            // client gathering and restoring a streaming session must still run.
            if (RecoverWindowsWhenDisplayDisappearsCheckBox.IsChecked != true) return;
            var result = WindowPlacementService.RecoverOffscreenApplicationWindows();
            if (result.Moved > 0 || result.Failed > 0)
            {
                DisplayApplyStatusText.Text = $"Recovered {result.Moved} hidden app windows to the primary display.";
                if (result.Failed > 0) DisplayApplyStatusText.Text = $"已移回 {result.Moved} 个窗口，另有 {result.Failed} 个未能移动。";
                DisplayApplyStatusText.Foreground = ResourceBrush(result.Failed > 0 ? "WarningBrush" : "SuccessBrush");
                await LogWindowResultAsync("window.auto_recover",
                    $"Automatic window recovery completed. Moved={result.Moved}; Failed={result.Failed}", result.Failed);
            }
        }
        catch (Exception exception)
        {
            await _log.WriteAsync($"Automatic window recovery failed: {DescribeExceptionForLog(exception)}");
        }
    }

    private async void RepairDesktop_Click(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        button.IsEnabled = false;
        try {
            DisplayApplyStatusText.Text = await DisplayRecoveryJournal.RepairDesktopAsync();
            DisplayApplyStatusText.Foreground = ResourceBrush("SuccessBrush");
        } catch (Exception error) {
            DisplayApplyStatusText.Text = error.Message;
            DisplayApplyStatusText.Foreground = ResourceBrush("WarningBrush");
        } finally { button.IsEnabled = true; }
    }

    private async void RecoverWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = WindowPlacementService.RecoverOffscreenApplicationWindows();
            DisplayApplyStatusText.Text = result.Moved > 0
                ? $"Recovered {result.Moved} hidden app windows to the primary display."
                : "All open app windows are already reachable.";
            if (result.Failed > 0) DisplayApplyStatusText.Text = $"已移回 {result.Moved} 个窗口，另有 {result.Failed} 个未能移动。";
            DisplayApplyStatusText.Foreground = ResourceBrush(result.Failed > 0 ? "WarningBrush" : "SuccessBrush");
            await LogWindowResultAsync("window.manual_recover",
                $"Manual window recovery completed. Moved={result.Moved}; Reachable={result.AlreadyOnTarget}; Failed={result.Failed}", result.Failed);
        }
        catch (Exception exception)
        {
            DisplayApplyStatusText.Text = $"Window recovery failed: {exception.Message}";
            DisplayApplyStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Manual window recovery failed: {DescribeExceptionForLog(exception)}");
        }
    }

    private async Task LoadDisplaySettingsAsync()
    {
        if (_remoteDisplaySettingsBusy) return;
        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null || !_sunshineAuthenticated)
        {
            _displayState = null;
            InventoryDisplayList.ItemsSource = null;
            DisplayInventoryStatusText.Text = "Setup required";
            DisplayInventoryStatusDetail.Text = "Set up secure Sunshine control before choosing a streaming display.";
            DisplayInventoryStatusDot.Background = ResourceBrush("WarningBrush");
            VddStatusText.Text = "Display inventory unavailable";
            VddStatusDetail.Text = "PiuLink reads display information locally after Sunshine secure control is connected.";
            StreamingDisplayComboBox.ItemsSource = new[]
            {
                new DisplayChoice("Automatic · Sunshine chooses", null, null)
            };
            StreamingDisplayComboBox.SelectedIndex = 0;
            UpdateDisplayControls();
            LocalizeProperty(DisplayInventoryStatusText, TextBlock.TextProperty);
            LocalizeProperty(DisplayInventoryStatusDetail, TextBlock.TextProperty);
            LocalizeProperty(VddStatusText, TextBlock.TextProperty);
            LocalizeProperty(VddStatusDetail, TextBlock.TextProperty);
            LocalizeProperty(DisplayApplyStatusText, TextBlock.TextProperty);
            return;
        }

        _sunshineCredential = credential;
        _displaySettingsBusy = true;
        DisplayInventoryStatusText.Text = "Detecting";
        DisplayInventoryStatusDetail.Text = "Reading Windows display devices from Sunshine…";
        DisplayInventoryStatusDot.Background = ResourceBrush("PrimaryBrush");
        DisplayApplyStatusText.Text = "Checking the current streaming target…";
        DisplayApplyStatusText.Foreground = ResourceBrush("MutedBrush");
        UpdateDisplayControls();

        try
        {
            var state = await _sunshineApi.GetDisplayStateAsync(
                credential.Username,
                credential.Secret);
            state = WithActiveCapture(state, DisplayRecoveryJournal.Read());
            _displayState = state;

            InventoryDisplayList.ItemsSource = state.Displays
                .OrderBy(display => display.Bounds?.X ?? int.MaxValue)
                .ThenBy(display => display.Bounds?.Y ?? int.MaxValue)
                .Select(FormatDisplayChoice).ToArray();
            var choices = new List<DisplayChoice>
            {
                new("Automatic · Sunshine chooses", null, null)
            };
            choices.AddRange(state.Displays
                .OrderByDescending(display => display.IsPrimary)
                .ThenBy(display => display.IsVirtual)
                .ThenBy(display => display.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .Select(display => new DisplayChoice(
                    FormatDisplayChoice(display),
                    display.DeviceId,
                    display)));

            var desiredDeviceId = _pendingDisplaySettings is null
                ? state.SelectedDeviceId
                : _pendingDisplaySettings.DeviceId;
            var selectedChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.DeviceId, desiredDeviceId, StringComparison.OrdinalIgnoreCase));
            if (selectedChoice is null && !string.IsNullOrWhiteSpace(desiredDeviceId))
            {
                selectedChoice = new DisplayChoice(
                    _pendingDisplaySettings is null
                        ? "Unavailable display · currently configured"
                        : "Unavailable display · queued for next connection",
                    desiredDeviceId,
                    null);
                choices.Add(selectedChoice);
            }

            // A detected VDD is the PiuLink default on an unconfigured Sunshine installation.
            // Existing user selections always win.
            selectedChoice ??= choices.FirstOrDefault(choice => choice.Display?.IsVirtual == true);
            StreamingDisplayComboBox.ItemsSource = choices;
            StreamingDisplayComboBox.SelectedItem = selectedChoice ?? choices[0];
            ExclusiveDisplayCheckBox.IsChecked = selectedChoice?.DeviceId is not null &&
                                                 (_pendingDisplaySettings?.ExclusiveDisplay ??
                                                  (state.SelectedDeviceId is null && selectedChoice.Display?.IsVirtual == true || state.ExclusiveDisplay));
            SmartDisplayCheckBox.IsChecked = _pendingDisplaySettings?.SmartDisplay ??
                                             (selectedChoice?.Display?.IsVirtual == true
                                                 ? state.SelectedDeviceId is null
                                                     ? HostPreferences.SmartDisplayEnabled
                                                     : state.SmartDisplay
                                                 : HostPreferences.SmartDisplayEnabled);

            if (_pendingDisplaySettings is not null)
            {
                DisplayApplyStatusText.Text = _sunshineOverview?.HasActiveStreams == true
                    ? "Display change queued. The current stream is unchanged; it will apply after disconnect."
                    : "Display change queued. PiuLink is applying it before the next connection…";
                DisplayApplyStatusText.Foreground = ResourceBrush("WarningBrush");
            }

            var physicalCount = state.Displays.Count(display => !display.IsVirtual);
            var virtualDisplays = state.Displays.Where(display => display.IsVirtual).ToArray();
            DisplayInventoryStatusText.Text = state.Displays.Count == 0 ? "None found" : "Ready";
            DisplayInventoryStatusDetail.Text = state.Displays.Count == 0
                ? "Sunshine did not report an active Windows display."
                : string.Format(LocalizationService.Instance.TranslateLiteral("{0} physical · {1} virtual"), physicalCount, virtualDisplays.Length);
            DisplayInventoryStatusDot.Background = state.Displays.Count == 0
                ? ResourceBrush("WarningBrush")
                : ResourceBrush("SuccessBrush");

            if (virtualDisplays.Length == 0)
            {
                var managedVirtualDisplay = _virtualDisplayControl.Inspect();
                if (managedVirtualDisplay.Enabled)
                {
                    QueueDisplayInventoryRefresh();
                    VddStatusText.Text = "Virtual display is waiting for Sunshine";
                    VddStatusDetail.Text = _sunshineOverview?.HasActiveStreams == true
                        ? "The driver is active. PiuLink will refresh Sunshine after the current stream disconnects."
                        : "The driver is active. PiuLink is refreshing Sunshine so it appears in this list…";
                }
                else
                {
                    VddStatusText.Text = "No virtual display detected";
                    VddStatusDetail.Text = "Physical displays are available. Install a trusted VDD separately if you need a headless target.";
                }
            }
            else
            {
                VddStatusText.Text = virtualDisplays.Length == 1
                    ? $"Virtual display ready · {virtualDisplays[0].FriendlyName}"
                    : $"{virtualDisplays.Length} virtual displays ready";
                VddStatusDetail.Text = "Select a virtual display above to use it as the Sunshine streaming target.";
            }

            if (_pendingDisplaySettings is null) DisplayApplyStatusText.Text = "Select a screen in the client's screen panel to switch during a stream.";
            await _log.WriteAsync(
                $"Display inventory loaded. Total={state.Displays.Count}; Physical={physicalCount}; Virtual={virtualDisplays.Length}; Exclusive={state.ExclusiveDisplay}");
        }
        catch (Exception exception)
        {
            _displayState = null;
            InventoryDisplayList.ItemsSource = null;
            DisplayInventoryStatusText.Text = "Unavailable";
            DisplayInventoryStatusDetail.Text = DescribeSunshineFailure(exception);
            DisplayInventoryStatusDot.Background = ResourceBrush("ErrorBrush");
            DisplayApplyStatusText.Text = "PiuLink could not read Sunshine display settings.";
            DisplayApplyStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Sunshine display inventory failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _displaySettingsBusy = false;
            UpdateDisplayControls();
            LocalizeProperty(DisplayInventoryStatusText, TextBlock.TextProperty);
            LocalizeProperty(DisplayInventoryStatusDetail, TextBlock.TextProperty);
            LocalizeProperty(VddStatusText, TextBlock.TextProperty);
            LocalizeProperty(VddStatusDetail, TextBlock.TextProperty);
            LocalizeProperty(DisplayApplyStatusText, TextBlock.TextProperty);
        }
    }

    private async void ApplyDisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        _displayAutoSaveCancellation?.Cancel();
        await ApplySelectedDisplaySettingsAsync(showQueuedConfirmation: true);
    }

    private void ScheduleDisplaySettingsAutoSave()
    {
        if (_displaySettingsBusy || _remoteDisplaySettingsBusy ||
            !_sunshineAuthenticated ||
            StreamingDisplayComboBox.SelectedItem is not DisplayChoice choice)
        {
            return;
        }

        SetPendingDisplaySettings(CreatePendingDisplaySettings(choice));

        _displayAutoSaveCancellation?.Cancel();
        _displayAutoSaveCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _displayAutoSaveCancellation = cancellation;
        DisplayApplyStatusText.Text = _sunshineOverview?.HasActiveStreams == true
            ? "Display change queued. The current stream is unchanged; it will apply after disconnect."
            : "Display choice changed. Saving automatically…";
        DisplayApplyStatusText.Foreground = _sunshineOverview?.HasActiveStreams == true
            ? ResourceBrush("WarningBrush")
            : ResourceBrush("PrimaryBrush");
        _ = SaveDisplaySettingsAfterDelayAsync(cancellation);
    }

    private async Task SaveDisplaySettingsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                await ApplySelectedDisplaySettingsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this pending save.
        }
        finally
        {
            if (ReferenceEquals(_displayAutoSaveCancellation, cancellation))
            {
                _displayAutoSaveCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async void PendingDisplaySettingsTimer_Tick(object? sender, EventArgs e)
    {
        if ((_pendingDisplaySettings is null && !_pendingDisplayInventoryRefresh) ||
            _remoteDisplaySettingsBusy ||
            _pendingDisplaySettingsApplyBusy ||
            _displaySettingsBusy ||
            _sunshineConfigurationBusy ||
            DisplayRecoveryJournal.Pending ||
            !_sunshineAuthenticated)
        {
            return;
        }

        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null)
        {
            return;
        }

        _pendingDisplaySettingsApplyBusy = true;
        try
        {
            var overview = await _sunshineApi.GetOverviewAsync(
                credential.Username,
                credential.Secret);
            if (_remoteDisplaySettingsBusy || _sunshineConfigurationBusy || DisplayRecoveryJournal.Pending) return;
            RenderSunshineOverview(overview);
            if (overview.HasActiveStreams || overview.HasRetainedApplication)
            {
                if (_pendingDisplayInventoryRefresh)
                {
                    VddStatusText.Text = "Virtual display refresh queued";
                    VddStatusDetail.Text = "The current stream is protected. Sunshine will refresh its display list after disconnect.";
                }

                return;
            }

            if (_pendingDisplayInventoryRefresh)
            {
                await RefreshSunshineDisplayInventoryAsync(credential);
            }

            if (_pendingDisplaySettings is not null && !DisplayRecoveryJournal.Pending)
            {
                await ApplySelectedDisplaySettingsAsync();
            }
        }
        catch (Exception exception)
        {
            await _log.WriteAsync(
                $"Queued display settings check failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _pendingDisplaySettingsApplyBusy = false;
        }
    }

    private void QueueDisplayInventoryRefresh()
    {
        if (_displayInventoryRefreshAttempted)
        {
            return;
        }

        _pendingDisplayInventoryRefresh = true;
        HostPreferences.PendingDisplayInventoryRefresh = true;
    }

    private async Task RefreshSunshineDisplayInventoryAsync(SunshineDeviceCredential credential)
    {
        if (_displayInventoryRefreshAttempted || !_pendingDisplayInventoryRefresh)
        {
            return;
        }

        if (!await _remoteDisplayApplyGate.WaitAsync(0))
        {
            return;
        }
        try
        {
            if (_displayInventoryRefreshAttempted || !_pendingDisplayInventoryRefresh ||
                _remoteDisplaySettingsBusy || _sunshineConfigurationBusy || DisplayRecoveryJournal.Pending)
            {
                return;
            }

            _displayInventoryRefreshAttempted = true;
            _displaySettingsBusy = true;
            VddStatusText.Text = "Refreshing Sunshine display list…";
            VddStatusDetail.Text = "The virtual display is active in Windows. Sunshine is restarting once to detect it.";
            DisplayApplyStatusText.Text = "Refreshing Sunshine so it can detect the virtual display…";
            DisplayApplyStatusText.Foreground = ResourceBrush("PrimaryBrush");
            UpdateDisplayControls();

            try
            {
                await _sunshineApi.RestartAsync(credential.Username, credential.Secret);
                var overview = await GetSunshineOverviewWithRetryAsync(credential);
                RenderSunshineOverview(overview);
                _displaySettingsBusy = false;
                await LoadDisplaySettingsAsync();
                _pendingDisplayInventoryRefresh = false;
                HostPreferences.PendingDisplayInventoryRefresh = false;

                var virtualCount = _displayState?.Displays.Count(display => display.IsVirtual) ?? 0;
                if (virtualCount > 0)
                {
                    VddStatusText.Text = virtualCount == 1
                        ? $"Virtual display ready · {_displayState!.Displays.First(display => display.IsVirtual).FriendlyName}"
                        : $"{virtualCount} virtual displays ready";
                    VddStatusDetail.Text = "Sunshine refreshed successfully. Select the virtual display above as the streaming target.";
                    DisplayApplyStatusText.Text = "Virtual display detected. It is ready to select.";
                    DisplayApplyStatusText.Foreground = ResourceBrush("SuccessBrush");
                    await _log.WriteAsync($"Sunshine display inventory refresh completed. Virtual={virtualCount}");
                }
                else
                {
                    VddStatusText.Text = "Windows enabled the virtual display, but Sunshine did not detect it";
                    VddStatusDetail.Text = "Use Refresh to try again, or restart Windows if the driver was just installed.";
                    DisplayApplyStatusText.Text = "Sunshine refreshed, but the virtual display is still unavailable.";
                    DisplayApplyStatusText.Foreground = ResourceBrush("WarningBrush");
                    await _log.WriteAsync("Sunshine display inventory refresh completed without a virtual display.");
                }
            }
            catch (Exception exception)
            {
                _displayInventoryRefreshAttempted = false;
                VddStatusText.Text = "Virtual display refresh will retry";
                VddStatusDetail.Text = DescribeSunshineFailure(exception);
                DisplayApplyStatusText.Text = "PiuLink could not refresh Sunshine yet. It will retry safely.";
                DisplayApplyStatusText.Foreground = ResourceBrush("WarningBrush");
                await _log.WriteAsync($"Sunshine display inventory refresh failed: {DescribeExceptionForLog(exception)}");
            }
            finally
            {
                _displaySettingsBusy = false;
                UpdateDisplayControls();
            }
        }
        finally
        {
            _remoteDisplayApplyGate.Release();
        }
    }

    private async Task ApplySelectedDisplaySettingsAsync(bool showQueuedConfirmation = false)
    {
        if (_displaySettingsBusy || _remoteDisplaySettingsBusy ||
            StreamingDisplayComboBox.SelectedItem is not DisplayChoice choice)
        {
            return;
        }

        var requestedSettings = CreatePendingDisplaySettings(choice);
        SetPendingDisplaySettings(requestedSettings);
        if (_sunshineOverview?.HasActiveStreams == true)
        {
            ShowQueuedDisplayConfirmation(showQueuedConfirmation);
            await _log.WriteAsync("Display settings were queued while a client was streaming.");
            UpdateDisplayControls();
            return;
        }

        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null)
        {
            DisplayApplyStatusText.Text = "Set up secure Sunshine control first.";
            DisplayApplyStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        try
        {
            _displaySettingsBusy = true;
            DisplayApplyStatusText.Text = "Checking that the current stream has ended…";
            DisplayApplyStatusText.Foreground = ResourceBrush("PrimaryBrush");
            UpdateDisplayControls();

            var freshOverview = await _sunshineApi.GetOverviewAsync(
                credential.Username,
                credential.Secret);
            RenderSunshineOverview(freshOverview);
            if (freshOverview.HasActiveStreams)
            {
                ShowQueuedDisplayConfirmation(showQueuedConfirmation);
                await _log.WriteAsync("Display settings were queued after a fresh active-stream check.");
                return;
            }

            DisplayApplyStatusText.Text = "Saving the display target and restarting Sunshine…";
            var exclusiveDisplay = requestedSettings.ExclusiveDisplay;
            var virtualTarget = exclusiveDisplay && choice.Display?.IsVirtual == true
                ? DisplayTopologyService.FindActiveVirtualTarget(
                    choice.Display.DisplayName, choice.Display.FriendlyName)
                : null;
            var shouldMoveWindows = choice.Display?.IsVirtual == true &&
                                    !exclusiveDisplay &&
                                    MoveWindowsToVirtualDisplayCheckBox.IsChecked == true;
            await _sunshineApi.ApplyDisplaySettingsAsync(
                requestedSettings.DeviceId,
                exclusiveDisplay,
                requestedSettings.SmartDisplay,
                credential.Username,
                credential.Secret);
            // Routing is already committed and verified. A desktop preparation failure must
            // not make the one-second pending timer restart Sunshine indefinitely.
            ClearPendingDisplaySettings();
            var overview = await GetSunshineOverviewWithRetryAsync(credential);
            RenderSunshineOverview(overview);
            if (exclusiveDisplay && choice.Display?.IsVirtual == true)
            {
                if (overview.HasCurrentApplication || overview.HasActiveStreams)
                    throw new InvalidOperationException("Display settings were saved. End the current session and save again to prepare the idle desktop.");
                if (virtualTarget is not null)
                    DisplayTopologyService.DisconnectVirtualDesktop(virtualTarget);
                var desktop = DisplayTopologyService.ReadDesktopState();
                WindowPlacementService.ObserveSessionDesktop(desktop.PhysicalOnly, desktop.Layout);
            }
            _displaySettingsBusy = false;
            await LoadDisplaySettingsAsync();
            if (shouldMoveWindows && choice.Display is not null)
            {
                DisplayApplyStatusText.Text = "Virtual display saved. Moving open app windows…";
                await Task.Yield();
                var moveResult = WindowPlacementService.MoveApplicationWindows(choice.Display.DisplayName);
                DisplayApplyStatusText.Text = moveResult.Moved > 0
                    ? $"Virtual display is active. Moved {moveResult.Moved} app windows."
                    : "Virtual display is active. Open app windows are already there.";
                if (moveResult.Failed > 0)
                {
                    DisplayApplyStatusText.Text += $" {moveResult.Failed} protected windows could not be moved.";
                }

                await LogWindowResultAsync("window.migrate",
                    $"Window migration completed. Target={choice.Display.DisplayName}; Moved={moveResult.Moved}; AlreadyOnTarget={moveResult.AlreadyOnTarget}; Failed={moveResult.Failed}", moveResult.Failed);
            }
            else
            {
                DisplayApplyStatusText.Text = choice.DeviceId is null
                    ? "Automatic display selection is active."
                    : exclusiveDisplay
                        ? "Single-display mode is armed. Other screens will turn off when streaming starts and return after disconnect."
                        : "Display saved. Other screens will remain available.";
            }
            DisplayApplyStatusText.Foreground = ResourceBrush("SuccessBrush");
            await _log.WriteAsync(
                $"Sunshine display routing applied. Automatic={choice.DeviceId is null}; Exclusive={exclusiveDisplay}; RestoreOnDisconnect={choice.DeviceId is not null}");
        }
        catch (Exception exception)
        {
            DisplayApplyStatusText.Text = DescribeSunshineFailure(exception);
            DisplayApplyStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Applying Sunshine display routing failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _displaySettingsBusy = false;
            UpdateDisplayControls();
        }
    }

    private static SunshineDisplayState WithActiveCapture(SunshineDisplayState state, DisplayRecoveryJournal.Entry? entry) =>
        entry is { PreparedDeviceId: not null, Phase: "Ready" or "Streaming" }
            ? state with { SelectedDeviceId = entry.PreparedDeviceId, ExclusiveDisplay = entry.Exclusive, SmartDisplay = false }
            : state;

    private static SunshineDisplay[] WithSessionLayout(IReadOnlyList<SunshineDisplay> displays,
        IReadOnlyDictionary<string, DesktopRectangle> original)
    {
        // After exclusive mode or disabling the primary, Windows can translate the desktop origin.
        // Put inactive screens back into the current coordinate frame using a surviving screen.
        var anchor = displays.FirstOrDefault(d => d.Bounds != null && original.ContainsKey(d.DeviceId));
        if (anchor == null) return displays.ToArray();
        var dx = anchor.Bounds!.X - original[anchor.DeviceId].X;
        var dy = anchor.Bounds.Y - original[anchor.DeviceId].Y;
        return displays.Select(d => d.Bounds == null && original.TryGetValue(d.DeviceId, out var old)
            ? d with { Bounds = old with { X = old.X + dx, Y = old.Y + dy } } : d).ToArray();
    }

    private async Task<DisplayCompanionServer.SessionDisplayState> HandleSessionDisplaysAsync(string owner,
        DisplayCompanionServer.SessionDisplayCommand command, CancellationToken token)
    {
        if (!DisplayRecoveryJournal.OwnsStream(owner, command.SessionId)) throw new UnauthorizedAccessException();
        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead() ?? throw new UnauthorizedAccessException();
        if (!await _remoteDisplayApplyGate.WaitAsync(0, token)) throw new InvalidOperationException("outputs_changed");
        try
        {
            using var writer = DisplayRecoveryJournal.AcquireSessionWriter();
            var entry = DisplayRecoveryJournal.Read();
            var status = await _sunshineApi.GetDisplaySessionStatusAsync(credential.Username, credential.Secret, token);
            if (entry == null || !DisplayRecoveryJournal.OwnsStream(owner, command.SessionId) ||
                status.Streams != 1) throw new UnauthorizedAccessException();
            var key = entry.CaptureKey;
            SunshineApiClient.VideoSize? encoded = null;
            Task<SunshineApiClient.VideoSize> VideoMode(SunshineApiClient.VideoSize? size) =>
                _sunshineApi.LiveVideoModeAsync(command.SessionId, owner, credential.Username, credential.Secret, size, CancellationToken.None);
            if (command.Action != "list")
            {
                if (key == null) throw new InvalidOperationException("output_protected");
                if (command.Action == "capture") {
                    var state = await _sunshineApi.GetDisplayStateAsync(credential.Username, credential.Secret, token);
                    var target = state.Displays.SingleOrDefault(d => d.Bounds != null &&
                        string.Equals(d.DeviceId, command.Key, StringComparison.OrdinalIgnoreCase));
                    if (target == null) throw new InvalidOperationException("outputs_changed");
                    var next = DisplayTopologyService.CaptureOutputKey(target.DisplayName, target.FriendlyName);
                    if (next == null) throw new InvalidOperationException("outputs_changed");
                    if (next != key) {
                        // Once dispatched, finish recording the acknowledged route even if the caller disconnects.
                        await _sunshineApi.RegisterPreparedDisplayLeaseAsync(command.SessionId, owner, credential.Username,
                            credential.Secret, CancellationToken.None, action: "capture", output: target.DeviceId);
                        DisplayRecoveryJournal.Save(entry with { CaptureKey = next, PreparedDeviceId = target.DeviceId });
                        key = next;
                    }
                }
                else if (command.Action == "resize")
                    DisplayTopologyService.ResizeVirtualDesktop(key, command.Key!, command.Width!.Value, command.Height!.Value);
                else if (command.Action == "stream-mode") {
                    if (!status.LiveVideoFrameRate || command.Key != key) throw new InvalidOperationException("outputs_changed");
                    try { encoded = await VideoMode(new(command.Width!.Value, command.Height!.Value, command.Fps!.Value)); }
                    catch (Exception ex) when (ex is HttpRequestException or System.IO.InvalidDataException) {
                        throw new InvalidOperationException("display_mode_unavailable", ex);
                    }
                }
                else if (command.Action == "resize-stream") {
                    if (!status.LiveVideoResize || command.Key != key) throw new InvalidOperationException("outputs_changed");
                    var before = DisplayTopologyService.GetVirtualDesktopMode(key)?.Current ?? throw new InvalidOperationException("output_protected");
                    var previousVideo = await VideoMode(null);
                    var next = new SunshineApiClient.VideoSize(command.Width!.Value, command.Height!.Value);
                    DisplayTopologyService.ResizeVirtualDesktop(key, command.Key, next.Width, next.Height);
                    try { encoded = await VideoMode(next); }
                    catch {
                        try { DisplayTopologyService.ResizeVirtualDesktop(key, command.Key, before.Width, before.Height); }
                        finally { await VideoMode(previousVideo); }
                        throw new InvalidOperationException("display_mode_unavailable");
                    }
                }
                else if (command.Action == "create") await _virtualDisplayControl.CreateOutputAsync(key, command.ExpectedCount!.Value, token);
                else DisplayTopologyService.SetSessionOutput(key, command.Key!, command.Action == "enable", original: entry.Desktop);
            }
            var outputs = DisplayTopologyService.SessionOutputs(key);
            var capability = _virtualDisplayControl.GetCreationCapability(outputs);
            var virtualDesktop = outputs.Any(o => o.Capture && o.Active && o.Virtual)
                ? DisplayTopologyService.GetVirtualDesktopMode(key) : null;
            if ((status.LiveVideoFrameRate || virtualDesktop != null && status.LiveVideoResize) && encoded == null) {
                try { encoded = await VideoMode(null); }
                catch (HttpRequestException) { /* The first encoded keyframe may still be pending. */ }
            }
            return new(outputs, capability.Count, capability.Status, virtualDesktop, encoded);
        }
        finally { _remoteDisplayApplyGate.Release(); }
    }

    private string? _gatherWindowsTarget;
    private bool _gatherWindowsApplied;

    private async Task<bool> ApplyRemoteDisplaySelectionAsync(
        string? deviceId,
        bool exclusive,
        int? width,
        int? height,
        bool gatherWindows,
        int? scalePercent,
        CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess()) {
            var owner = DisplayRequestContext.Owner.Value; var requestId = DisplayRequestContext.RequestId.Value;
            var fps = DisplayRequestContext.FramesPerSecond.Value; var mode = DisplayRequestContext.Mode.Value;
            return await Dispatcher.InvokeAsync(async () => {
                DisplayRequestContext.Owner.Value = owner; DisplayRequestContext.RequestId.Value = requestId;
                DisplayRequestContext.FramesPerSecond.Value = fps; DisplayRequestContext.Mode.Value = mode;
                return await ApplyRemoteDisplaySelectionAsync(deviceId, exclusive, width, height, gatherWindows, scalePercent, cancellationToken);
            }).Task.Unwrap();
        }
        if (!await _remoteDisplayApplyGate.WaitAsync(0, cancellationToken)) return false;
        var preparationTimer = Stopwatch.StartNew();
        bool transactionStarted = false;
        FileStream? preparationLock = null;
        try
        {
            _remoteDisplaySettingsBusy = true;
            _displayAutoSaveCancellation?.Cancel();
            GatherWindowsStatus.Text = "";
            RetryGatherWindowsButton.Visibility = Visibility.Collapsed;
            var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
            if (credential is null)
                throw new InvalidOperationException("PiuLink secure Sunshine control is not configured.");

            // A route change still waits for an active writer. Read-only background checks
            // observe _remoteDisplaySettingsBusy after their await and yield immediately.
            var idleDeadline = Environment.TickCount64 + 12000;
            while (_displaySettingsBusy || _pendingDisplaySettingsApplyBusy ||
                   _sessionGuardianBusy || _physicalDisplayRecoveryBusy || _virtualDisplayBusy)
            {
                if (Environment.TickCount64 >= idleDeadline)
                    throw new TimeoutException("Display maintenance is still running. Retry shortly.");
                await Task.Delay(100, cancellationToken);
            }

            // The independent recovery process can briefly restart the control endpoint.
            // Only repeat the read; no lease or display mutation has been made here.
            SunshineDisplayPreparation preparation;
            var probeDeadline = Environment.TickCount64 + 5000;
            for (;;) {
                if (DisplayRecoveryJournal.Read() is { Phase: "Restoring" }) throw new DisplayRecoveryInProgressException();
                try {
                    preparation = await _sunshineApi.GetDisplayPreparationAsync(credential.Username, credential.Secret, cancellationToken);
                    break;
                } catch (HttpRequestException error) when (error.StatusCode == null && Environment.TickCount64 < probeDeadline) {
                    await Task.Delay(200, cancellationToken);
                }
            }
            if (preparation.ActiveStreams != 0) return false;
            var retained = preparation.CurrentApplicationId != 0;
            var state = preparation.State;
            var target = string.IsNullOrWhiteSpace(deviceId)
                ? null
                : state.Displays.FirstOrDefault(display => string.Equals(
                    display.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (deviceId is not null && target is null)
                throw new InvalidOperationException("The requested display is no longer available.");
            if (width.HasValue && target?.IsVirtual != true)
                throw new InvalidOperationException("Custom display dimensions require a virtual display.");
            if (gatherWindows && (target is null || target.IsVirtual || exclusive ||
                !System.Windows.Forms.Screen.AllScreens.Any(screen =>
                    string.Equals(screen.DeviceName, target.DisplayName, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Choose an active physical display to gather windows.");

            var owner = DisplayRequestContext.Owner.Value ?? throw new InvalidOperationException("Missing authenticated display owner.");
            var requestId = DisplayRequestContext.RequestId.Value ?? throw new InvalidOperationException("Missing display request.");
            var smartDisplay = exclusive && target?.IsVirtual == true && width.HasValue && height.HasValue;
            var settingsMatch = string.Equals(state.SelectedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                state.ExclusiveDisplay == exclusive && state.SmartDisplay == smartDisplay && (deviceId is null || state.HdrAuto);
            var pending = DisplayRecoveryJournal.Read();
            if (pending is { Phase: "Fault" }) throw new DisplayRecoveryFaultException();
            if (pending is { Phase: "Ready" } && pending.Owner == owner && pending.RequestId == requestId &&
                pending.ExpiresAt > DateTimeOffset.UtcNow) return true;
            var switching = !retained && !exclusive && target is { IsVirtual: false } &&
                DisplayRecoveryJournal.CanSwitch(pending, owner) && pending!.Desktop.StreamBounds?.ContainsKey(target.DeviceId) == true;
            if (pending is not null && !switching && !(retained && DisplayRecoveryJournal.CanResume(pending, owner)))
                throw new DisplayRecoveryInProgressException();
            var sameRetainedRoute = pending?.PreparedDeviceId is { } previousDevice
                ? string.Equals(previousDevice, deviceId, StringComparison.OrdinalIgnoreCase) && pending.Exclusive == exclusive
                : settingsMatch;
            if (retained && !sameRetainedRoute) throw new RetainedDisplayRouteException();
            var before = DisplayTopologyService.CaptureDesktop();
            if (before.Paths.Length == 0) throw new InvalidOperationException("No recoverable desktop is available.");
            if (width.HasValue && !DisplayTopologyService.ValidateDisplayMode(
                DisplayTopologyService.ResolveDisplaySource(target!.DisplayName, target.FriendlyName),
                width.Value, height!.Value, DisplayRequestContext.FramesPerSecond.Value ?? 60))
                throw new InvalidOperationException("The selected target does not support this resolution and refresh rate.");
            before = before with { StreamBounds = state.Displays.Where(d => d.Bounds != null)
                .ToDictionary(d => d.DeviceId, d => d.Bounds!, StringComparer.OrdinalIgnoreCase) };
            preparationLock = DisplayRecoveryJournal.Begin(owner, requestId, before, preparation.Configuration, pending);
            if (pending is not null)
            {
                transactionStarted = true;
                await _sunshineApi.RegisterPreparedDisplayLeaseAsync(pending.RequestId, owner,
                    credential.Username, credential.Secret, cancellationToken, action: "abort");
            }
            transactionStarted = true;
            await _sunshineApi.RegisterPreparedDisplayLeaseAsync(DisplayRequestContext.RequestId.Value!, DisplayRequestContext.Owner.Value!,
                credential.Username, credential.Secret, cancellationToken, ready: false, mode: DisplayRequestContext.Mode.Value ?? "");
            if (switching && pending != null && (before.Layout != pending.Desktop.Layout ||
                !pending.Desktop.Scales.All(scale => before.Scales.TryGetValue(scale.Key, out var value) && value == scale.Value))) {
                if (!DisplayTopologyService.RestorePhysicalDesktop(pending.Desktop))
                    throw new InvalidOperationException("Windows could not restore the physical layout for switching displays.");
                preparation = await _sunshineApi.GetDisplayPreparationAsync(credential.Username, credential.Secret, cancellationToken);
                if (preparation.ActiveStreams != 0 || preparation.CurrentApplicationId != 0) throw new DisplayRecoveryInProgressException();
                state = preparation.State;
                target = state.Displays.SingleOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                    !d.IsVirtual && d.Bounds != null) ?? throw new InvalidOperationException("The selected physical display is no longer active.");
                before = DisplayTopologyService.CaptureDesktop();
            }
            _physicalDesktopSnapshot = pending?.Desktop ?? before;
            _physicalDisplayRecovery.CaptureBaseline(_physicalDesktopSnapshot.Layout);
            _pendingDisplayScale = null;
            if (scalePercent.HasValue)
            {
                if (target is null)
                    throw new InvalidOperationException("Choose a display before setting Windows scale.");
                if (!DisplayTopologyService.SupportedScalePercents.Contains(scalePercent.Value))
                    throw new ArgumentOutOfRangeException(nameof(scalePercent), "Unsupported Windows display scale.");
            }

            // The virtual desktop belongs to the client-selected merge mode. Keep it out of
            // the idle Windows topology; Sunshine will activate it when the stream starts and
            // restore the captured physical topology after the last client disconnects.
            var virtualTarget = exclusive && target?.IsVirtual == true
                ? DisplayTopologyService.FindActiveVirtualTarget(
                    target.DisplayName, target.FriendlyName)
                : null;

            var physicalPrepared = false;
            if (!settingsMatch && exclusive && target is { IsVirtual: false, Width: > 0, Height: > 0 } &&
                before.Paths.Length > 1)
            {
                // The client explicitly selected one physical monitor. Reserve and journal the
                // desktop first, then check what the engine can actually capture after Windows applies it.
                DisplayTopologyService.UsePhysicalDisplayOnly(target.DisplayName);
                physicalPrepared = true;
                preparation = await _sunshineApi.GetDisplayPreparationAsync(credential.Username, credential.Secret, cancellationToken);
                if (preparation.ActiveStreams != 0) throw new DisplayRecoveryInProgressException();
                state = preparation.State;
                target = state.Displays.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The selected physical display disappeared.");
            }
            var soleOutput = preparation.CanUseSolePhysicalOutput(target, physicalPrepared ? 1 : before.Paths.Length) &&
                DisplayTopologyService.IsOnlyActiveSource(target!.DisplayName);
            // A different GPU can require a new encoder probe. Keep the configuration path for those desktops.
            var sessionOutput = !settingsMatch && !soleOutput && preparation.CanCaptureExtendedOutput(target, exclusive) &&
                before.Paths.All(path => path.Source.Adapter.Equals(before.Paths[0].Source.Adapter));
            // A retained game must never be restarted on a failed fast-path check.
            if (retained && !settingsMatch && !soleOutput && !sessionOutput) throw new RetainedDisplayRouteException();
            if (settingsMatch || soleOutput || sessionOutput)
            {
                // Configuration equality does not imply that the idle Windows topology is
                // ready. A previous crash may have left the VDD attached after streaming.
                if (!physicalPrepared) PrepareClientManagedVirtualDesktop(target, exclusive, virtualTarget, width, height);
                if (soleOutput && !DisplayTopologyService.IsOnlyActiveSource(target!.DisplayName))
                    throw new InvalidOperationException("The capture desktop changed during preparation.");
                _displayState = state with { SelectedDeviceId = deviceId, ExclusiveDisplay = exclusive, SmartDisplay = smartDisplay };
                ApplyPreparedDisplayScale(target, exclusive, scalePercent);
                _gatherWindowsTarget = gatherWindows ? target!.DisplayName : null;
                _gatherWindowsApplied = false;
                ClearPendingDisplaySettings();
                await RegisterPreparedDisplayLeaseAsync(credential, target, exclusive, cancellationToken, transientRoute: !settingsMatch,
                    output: sessionOutput ? target!.DeviceId : null);
                var route = sessionOutput ? "session capture output" : soleOutput ? "sole physical output" : "configured output";
                await _log.WriteAsync($"Remote display ready in {preparationTimer.ElapsedMilliseconds} ms. Route={route}; engine restart=false.");
                return true;
            }

            await _sunshineApi.ApplyDisplaySettingsAsync(
                deviceId,
                exclusive,
                smartDisplay,
                credential.Username,
                credential.Secret,
                cancellationToken);
            PrepareClientManagedVirtualDesktop(target, exclusive, virtualTarget, width, height);
            _displayState = await _sunshineApi.GetDisplayStateAsync(
                credential.Username,
                credential.Secret,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                target = _displayState.Displays.FirstOrDefault(d =>
                    string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) ?? target;
            }
            ApplyPreparedDisplayScale(target, exclusive, scalePercent);
            _gatherWindowsTarget = gatherWindows ? target!.DisplayName : null;
            _gatherWindowsApplied = false;
            ClearPendingDisplaySettings();
            await _log.WriteAsync(
                $"Android prepared display routing. Device={deviceId ?? "automatic"}; " +
                $"Exclusive={exclusive}; Resolution={(width.HasValue ? $"{width}x{height}" : "stream default")}; " +
                $"Scale={(scalePercent.HasValue ? $"{scalePercent}%" : "unchanged")}; " +
                $"Elapsed={preparationTimer.ElapsedMilliseconds}ms");
            await RegisterPreparedDisplayLeaseAsync(credential, target, exclusive, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            var failed = transactionStarted ? DisplayRecoveryJournal.Read() : null;
            preparationLock?.Dispose(); preparationLock = null;
            if (failed is not null) await DisplayRecoveryJournal.RestoreAsync(observed: failed);
            await _log.WriteAsync($"Remote display preparation failed: Device={deviceId ?? "automatic"}; Exclusive={exclusive}; {DescribeExceptionForLog(exception)}");
            throw;
        }
        finally
        {
            preparationLock?.Dispose();
            _remoteDisplaySettingsBusy = false;
            _remoteDisplayApplyGate.Release();
        }
    }

    private void ApplyPreparedDisplayScale(SunshineDisplay? currentTarget, bool exclusive, int? scalePercent)
    {
        if (!scalePercent.HasValue || currentTarget is null) return;
        var activeSource = (exclusive && currentTarget.IsVirtual)
            ? (DisplayTopologyService.GetSingleActiveVirtualDisplayName() ?? currentTarget.DisplayName)
            : currentTarget.DisplayName;
        try
        {
            if (DisplayTopologyService.SetDisplayScale(activeSource, scalePercent.Value)) return;
            if (currentTarget.IsVirtual)
            {
                _pendingDisplayScale = new PendingDisplayScale(currentTarget.DeviceId, true, scalePercent.Value, DateTimeOffset.UtcNow.AddSeconds(15));
                return;
            }
        }
        catch (Exception exception) when (currentTarget.IsVirtual && exception is not ArgumentOutOfRangeException)
        {
            _pendingDisplayScale = new PendingDisplayScale(currentTarget.DeviceId, true, scalePercent.Value, DateTimeOffset.UtcNow.AddSeconds(15));
            return;
        }
        throw new InvalidOperationException("Windows scale is unavailable for this inactive target. Retry after the display is active.");
    }

    private async Task RegisterPreparedDisplayLeaseAsync(SunshineDeviceCredential credential, SunshineDisplay? target, bool exclusive, CancellationToken token, bool transientRoute = false, string? output = null)
    {
        var entry = DisplayRecoveryJournal.Read() ?? throw new InvalidOperationException("Display preparation expired.");
        if (DisplayRecoveryJournal.IsAborted(entry.Owner, entry.RequestId) || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new OperationCanceledException("Display preparation was cancelled or expired.");
        await _sunshineApi.RegisterPreparedDisplayLeaseAsync(entry.RequestId, entry.Owner, credential.Username, credential.Secret, token, mode: DisplayRequestContext.Mode.Value ?? "", output: output);
        if (DisplayRecoveryJournal.IsAborted(entry.Owner, entry.RequestId)) throw new OperationCanceledException("Display preparation was cancelled.");
        var captureSource = (exclusive && target?.IsVirtual == true)
            ? (DisplayTopologyService.GetSingleActiveVirtualDisplayName() ?? target.DisplayName)
            : target?.DisplayName;
        DisplayRecoveryJournal.MarkReady(entry.Owner, entry.RequestId, DisplayTopologyService.CaptureOutputKey(captureSource, target?.FriendlyName), target?.DeviceId, exclusive, transientRoute);
    }
    private async Task TryApplyPendingDisplayScaleAsync(string? activeVirtualDisplayName)
    {
        var pending = _pendingDisplayScale;
        if (pending is null || !pending.IsVirtual || string.IsNullOrWhiteSpace(activeVirtualDisplayName)) return;
        if (DateTimeOffset.UtcNow >= pending.ExpiresAt || !string.Equals(pending.DeviceId, _displayState?.SelectedDeviceId, StringComparison.OrdinalIgnoreCase)) {
            _pendingDisplayScale = null; return;
        }
        var pendingTarget = _displayState?.Displays.FirstOrDefault(display => display.DeviceId == pending.DeviceId);
        if (pendingTarget is null || !DisplayTopologyService.MatchesRequestedDisplay(activeVirtualDisplayName,
            pendingTarget.FriendlyName, pendingTarget.DisplayName, pendingTarget.FriendlyName)) return;
        try
        {
            if (!DisplayTopologyService.SetDisplayScale(activeVirtualDisplayName, pending.Percent)) return;
            _pendingDisplayScale = null;
            await _log.WriteAsync(
                $"Applied queued Windows display scale. Device={pending.DeviceId}; Scale={pending.Percent}%");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            _pendingDisplayScale = null;
            await _log.WriteAsync($"Queued display scale rejected by this display: {DescribeExceptionForLog(exception)}");
        }
        catch (Exception exception)
        {
            // The VDD can report active a moment before its DPI source is ready. Keep the
            // request queued and retry on the next bounded recovery tick.
            await _log.WriteAsync($"Queued display scale deferred: {DescribeExceptionForLog(exception)}");
        }
    }

    private void PrepareClientManagedVirtualDesktop(
        SunshineDisplay? target,
        bool exclusive,
        string? activeVirtualTarget,
        int? width,
        int? height)
    {
        if (exclusive && target?.IsVirtual == true &&
            DisplayTopologyService.IsVirtualDesktopOnly(target.DisplayName, target.FriendlyName))
        {
            if (_physicalDesktopSnapshot is null)
                throw new InvalidOperationException(
                    "PiuLink cannot reuse the virtual desktop without a physical recovery snapshot.");
            ApplyRequestedVirtualDisplayMode(width, height);
            return;
        }
        if (activeVirtualTarget is not null)
            DisplayTopologyService.DisconnectVirtualDesktop(activeVirtualTarget);
        if (!exclusive || target?.IsVirtual != true)
        {
            if (exclusive && target is { IsVirtual: false }) DisplayTopologyService.UsePhysicalDisplayOnly(target.DisplayName);
            return;
        }

        var conflicts = DisplayTopologyService.GetActiveVirtualDisplays()
            .Where(display => !DisplayTopologyService.MatchesRequestedDisplay(
                display.DisplayName,
                display.FriendlyName,
                target.DisplayName,
                target.FriendlyName))
            .ToArray();
        if (conflicts.Length > 0)
        {
            var names = string.Join(", ", conflicts.Select(display =>
                string.IsNullOrWhiteSpace(display.AdapterName)
                    ? display.FriendlyName
                    : display.AdapterName).Distinct(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"Another virtual display driver is controlling the desktop ({names}). " +
                "Stop its background service before starting an exclusive PiuLink stream.");
        }

        var desktop = DisplayTopologyService.ReadDesktopState();
        // Capture immediately while we know the desktop contains only physical paths. The
        // stream may start before the periodic recovery observer has captured a baseline.
        _physicalDesktopSnapshot ??= DisplayTopologyService.CaptureDesktop();
        if (desktop.PhysicalOnly)
        {
            _physicalDisplayRecovery.CaptureBaseline(desktop.Layout);
            WindowPlacementService.CaptureSessionDesktop(desktop.Layout);
        }
        else
            WindowPlacementService.ObserveSessionDesktop(false, desktop.Layout);
        if (_physicalDesktopSnapshot is null)
            throw new InvalidOperationException(
                "PiuLink could not capture the physical desktop before activating the virtual display.");

        DisplayTopologyService.ActivateVirtualDesktopOnly(target.DisplayName, target.FriendlyName);
        if (!DisplayTopologyService.IsVirtualDesktopOnly(target.DisplayName, target.FriendlyName))
            throw new InvalidOperationException(
                "Windows did not activate the selected virtual display as the exclusive desktop.");
        ApplyRequestedVirtualDisplayMode(width, height);
        WindowPlacementService.BeginSessionDesktopTransition();
    }

    private static void ApplyRequestedVirtualDisplayMode(int? width, int? height)
    {
        if (!width.HasValue || !height.HasValue) return;
        var displayName = DisplayTopologyService.GetSingleActiveVirtualDisplayName();
        if (displayName is null ||
            !DisplayTopologyService.SetTemporaryDisplayMode(displayName, width.Value, height.Value, DisplayRequestContext.FramesPerSecond.Value ?? 60))
            throw new InvalidOperationException(
                $"The virtual display does not support {width.Value}x{height.Value}.");
    }

    private PendingDisplaySettings CreatePendingDisplaySettings(DisplayChoice choice) => new(
        choice.DeviceId,
        choice.Display?.IsVirtual == true && ExclusiveDisplayCheckBox.IsChecked == true,
        choice.Display?.IsVirtual == true && SmartDisplayCheckBox.IsChecked == true);

    private void SetPendingDisplaySettings(PendingDisplaySettings pending)
    {
        _pendingDisplaySettings = pending;
        HostPreferences.PendingDisplayChange = pending;
    }

    private void ClearPendingDisplaySettings()
    {
        _pendingDisplaySettings = null;
        HostPreferences.PendingDisplayChange = null;
    }

    private void ShowQueuedDisplayConfirmation(bool showTrayNotification)
    {
        DisplayApplyStatusText.Text = "Saved for next connection. The current stream will continue unchanged.";
        DisplayApplyStatusText.Foreground = ResourceBrush("SuccessBrush");
        ApplyDisplaySettingsButton.Content = "Queued for next connection ✓";
        if (showTrayNotification)
        {
            _trayIcon.ShowBalloonTip(
                2600,
                LocalizationService.Instance.TranslateLiteral("Display change queued"),
                LocalizationService.Instance.TranslateLiteral(
                    "The current stream is unchanged. PiuLink will apply this display after disconnect."),
                Forms.ToolTipIcon.Info);
        }
    }

    private void UpdateDisplayControls()
    {
        var choice = StreamingDisplayComboBox.SelectedItem as DisplayChoice;
        var hasActiveStream = _sunshineOverview?.HasActiveStreams == true;
        var hasCredential = _sunshineAuthenticated && _sunshineCredential is not null;
        var sunshineInstalled = _snapshot?.Sunshine.State != ComponentState.Missing;

        StreamingDisplayComboBox.IsEnabled = false;
        ExclusiveDisplayCheckBox.IsEnabled = false;
        SmartDisplayCheckBox.IsEnabled = false;
        MoveWindowsToVirtualDisplayCheckBox.IsEnabled = false;
        ApplyDisplaySettingsButton.IsEnabled = false;
        ApplyDisplaySettingsButton.Content = hasActiveStream
            ? _pendingDisplaySettings is null
                ? "Use on next connection"
                : "Queued for next connection ✓"
            : "Save now";
        RefreshDisplaysButton.IsEnabled = !_displaySettingsBusy && hasCredential;

        SelectedDisplayDetailText.Text = LocalizationService.Instance.TranslateLiteral(choice switch
        {
            null => "No display selected.",
            { DeviceId: null } => "No client has selected a display yet.",
            { Display: not null } => FormatDisplayDetail(choice.Display),
            _ => "The last selected display is offline."
        });

        if (choice?.DeviceId is null)
        {
            ExclusiveDisplayCheckBox.IsChecked = false;
        }

        if (hasActiveStream)
        {
            DisplayApplyStatusText.Text = _pendingDisplaySettings is null
                ? "Select a screen in the client's screen panel to switch during a stream."
                : "Saved for next connection. The current stream will continue unchanged.";
            DisplayApplyStatusText.Foreground = _pendingDisplaySettings is null
                ? ResourceBrush("WarningBrush")
                : ResourceBrush("SuccessBrush");
        }
    }

    private static string FormatDisplayChoice(SunshineDisplay display)
    {
        var role = display.IsVirtual
            ? "Virtual display"
            : display.IsPrimary
                ? "Windows primary"
                : "Physical display";
        var refresh = display.RefreshRate.HasValue
            ? $" · {display.RefreshRate.Value:0.##} Hz"
            : string.Empty;
        var name = string.IsNullOrWhiteSpace(display.FriendlyName) ? display.DisplayName : display.FriendlyName;
        return $"{name} · {LocalizationService.Instance.TranslateLiteral(role)} · {LocalizationService.Instance.TranslateLiteral(display.ResolutionText)}{refresh}";
    }

    private static string FormatDisplayDetail(SunshineDisplay display)
    {
        return FormatDisplayChoice(display);
    }

    private void OpenWindowsDisplaySettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });

    private void OpenVirtualDisplaySetup_Click(object sender, RoutedEventArgs e)
    {
        VirtualDisplayNavButton.IsChecked = true;
    }

    private void OpenDisplaysPage_Click(object sender, RoutedEventArgs e)
    {
        DisplaysNavButton.IsChecked = true;
    }

    private void CheckVirtualDisplay_Click(object sender, RoutedEventArgs e)
    {
        LoadVirtualDisplayStatus();
        VirtualDisplayActionStatusText.Text = _virtualDisplayStatus?.Installed == true
            ? "Driver check complete. You can manage the virtual display above."
            : "The supported driver is not installed yet. Complete the steps below, then check again.";
        VirtualDisplayActionStatusText.Foreground = _virtualDisplayStatus?.Installed == true
            ? ResourceBrush("SuccessBrush")
            : ResourceBrush("WarningBrush");
    }

    private void LoadVirtualDisplayStatus()
    {
        try
        {
            _virtualDisplayStatus = _virtualDisplayControl.Inspect();
            RenderVirtualDisplayStatus(_virtualDisplayStatus);
        }
        catch (Exception exception)
        {
            _virtualDisplayStatus = null;
            VirtualDisplayManagerStatusText.Text = UiText("Driver status unavailable");
            VirtualDisplayManagerStatusDetail.Text = UiText("PiuLink could not inspect the supported virtual display driver.");
            VirtualDisplayManagerStateLabel.Text = UiText("Unavailable");
            VirtualDisplayManagerStatusDot.Background = ResourceBrush("ErrorBrush");
            CreateVirtualDisplayButton.IsEnabled = false;
            DisableVirtualDisplayButton.IsEnabled = false;
            _ = _log.WriteAsync($"Virtual display inspection failed: {DescribeExceptionForLog(exception)}");
        }
    }

    private void RenderVirtualDisplayStatus(VirtualDisplayDeviceStatus status)
    {
        if (!status.Installed)
        {
            VirtualDisplayManagerStatusText.Text = UiText("Virtual display driver not installed");
            VirtualDisplayManagerStatusDetail.Text = UiText("Follow the official installation guide below, then return to PiuLink.");
            VirtualDisplayManagerStateLabel.Text = UiText("Not installed");
            VirtualDisplayManagerStatusDot.Background = ResourceBrush("WarningBrush");
            CreateVirtualDisplayButton.IsEnabled = false;
            DisableVirtualDisplayButton.IsEnabled = false;
            return;
        }

        if (status.Enabled)
        {
            var idle = UsesExclusiveVirtualDesktop && DisplayTopologyService.IsPhysicalDesktopOnly();
            VirtualDisplayManagerStatusText.Text = UiText(idle ? "Virtual display is ready for streaming" : "Virtual display is active");
            VirtualDisplayManagerStatusDetail.Text = UiText(idle
                ? "Disconnected from the desktop. Sunshine will activate it when streaming starts."
                : string.Format(UiText("{0} is enabled and available to Windows."), status.DisplayName));
            VirtualDisplayManagerStateLabel.Text = UiText(idle ? "Ready" : "Active");
            VirtualDisplayManagerStatusDot.Background = ResourceBrush("SuccessBrush");
            CreateVirtualDisplayButton.IsEnabled = false;
            DisableVirtualDisplayButton.IsEnabled = !_virtualDisplayBusy;
            return;
        }

        VirtualDisplayManagerStatusText.Text = UiText("Driver installed · virtual display is off");
        VirtualDisplayManagerStatusDetail.Text = UiText(status.ProblemCode is > 0 and not 22
            ? string.Format(UiText("Windows reports driver problem code {0}. You can ask Windows to enable it again."), status.ProblemCode)
            : "Create the display when you want a private streaming screen.");
        VirtualDisplayManagerStateLabel.Text = UiText("Off");
        VirtualDisplayManagerStatusDot.Background = ResourceBrush("PrimaryBrush");
        CreateVirtualDisplayButton.IsEnabled = !_virtualDisplayBusy;
        DisableVirtualDisplayButton.IsEnabled = false;
    }

    private async void CreateVirtualDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_virtualDisplayBusy || _remoteDisplaySettingsBusy)
        {
            return;
        }

        _virtualDisplayBusy = true;
        CreateVirtualDisplayButton.IsEnabled = false;
        DisableVirtualDisplayButton.IsEnabled = false;
        VirtualDisplayActionStatusText.Text = "Waiting for Windows. Approve the administrator prompt to create the display…";
        VirtualDisplayActionStatusText.Foreground = ResourceBrush("PrimaryBrush");

        try
        {
            var result = await _virtualDisplayControl.SetEnabledAsync(enabled: true);
            _virtualDisplayStatus = result.Status;
            VirtualDisplayActionStatusText.Text = result.Message;
            VirtualDisplayActionStatusText.Foreground = result.Succeeded
                ? ResourceBrush("SuccessBrush")
                : result.Cancelled
                    ? ResourceBrush("WarningBrush")
                    : ResourceBrush("ErrorBrush");
            await _log.WriteAsync(
                $"Virtual display enable completed. Succeeded={result.Succeeded}; Cancelled={result.Cancelled}; Instance={result.Status.InstanceId ?? "none"}");

            if (result.Succeeded)
            {
                _displayInventoryRefreshAttempted = false;
                await Task.Delay(TimeSpan.FromMilliseconds(900));
                await RefreshAsync();
                if (_sunshineAuthenticated)
                {
                    await LoadDisplaySettingsAsync();
                }
            }
        }
        catch (Exception exception)
        {
            VirtualDisplayActionStatusText.Text = $"PiuLink could not create the display: {exception.Message}";
            VirtualDisplayActionStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Virtual display enable failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _virtualDisplayBusy = false;
            LoadVirtualDisplayStatus();
        }
    }

    private async void DisableVirtualDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_virtualDisplayBusy || _remoteDisplaySettingsBusy)
        {
            return;
        }

        if (_sunshineOverview?.HasActiveStreams == true)
        {
            VirtualDisplayActionStatusText.Text = "A client is streaming now. End the stream before closing its display.";
            VirtualDisplayActionStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        _virtualDisplayBusy = true;
        CreateVirtualDisplayButton.IsEnabled = false;
        DisableVirtualDisplayButton.IsEnabled = false;
        VirtualDisplayActionStatusText.Text = "Returning app windows to the primary display…";
        VirtualDisplayActionStatusText.Foreground = ResourceBrush("PrimaryBrush");

        try
        {
            var primaryScreen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.FirstOrDefault();
            var activeVirtualDisplay = _displayState?.Displays.FirstOrDefault(display =>
                display.IsVirtual && Forms.Screen.AllScreens.Any(screen =>
                    string.Equals(screen.DeviceName, display.DisplayName, StringComparison.OrdinalIgnoreCase)));
            if (primaryScreen is not null && activeVirtualDisplay is not null)
            {
                var moveResult = WindowPlacementService.MoveApplicationWindowsFromDisplay(
                    activeVirtualDisplay.DisplayName,
                    primaryScreen.DeviceName);
                await LogWindowResultAsync("window.virtual_close",
                    $"Virtual display close preflight. Source={activeVirtualDisplay.DisplayName}; Moved={moveResult.Moved}; Failed={moveResult.Failed}", moveResult.Failed);
            }

            VirtualDisplayActionStatusText.Text = "Waiting for Windows. Approve the administrator prompt to close the display…";
            var result = await _virtualDisplayControl.SetEnabledAsync(enabled: false);
            _virtualDisplayStatus = result.Status;

            if (result.Succeeded)
            {
                var recovery = WindowPlacementService.RecoverOffscreenApplicationWindows();
                VirtualDisplayActionStatusText.Text = recovery.Moved > 0
                    ? $"{result.Message} Recovered {recovery.Moved} remaining app windows."
                    : result.Message;
                VirtualDisplayActionStatusText.Foreground = ResourceBrush("SuccessBrush");
            }
            else
            {
                VirtualDisplayActionStatusText.Text = result.Message;
                VirtualDisplayActionStatusText.Foreground = result.Cancelled
                    ? ResourceBrush("WarningBrush")
                    : ResourceBrush("ErrorBrush");
            }

            await _log.WriteAsync(
                $"Virtual display disable completed. Succeeded={result.Succeeded}; Cancelled={result.Cancelled}; Instance={result.Status.InstanceId ?? "none"}");
            if (result.Succeeded)
            {
                _displayInventoryRefreshAttempted = false;
                _pendingDisplayInventoryRefresh = true;
                HostPreferences.PendingDisplayInventoryRefresh = true;
                await RefreshAsync();
                if (_sunshineAuthenticated)
                {
                    await LoadDisplaySettingsAsync();
                }
            }
        }
        catch (Exception exception)
        {
            VirtualDisplayActionStatusText.Text = $"PiuLink could not close the display: {exception.Message}";
            VirtualDisplayActionStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Virtual display disable failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _virtualDisplayBusy = false;
            LoadVirtualDisplayStatus();
        }
    }

    private void OpenVddReleases_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(OfficialLinks.VirtualDisplayDriverReleases)
        {
            UseShellExecute = true
        });

    private async void SubmitPairingPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = PairingPinBox.Text.Trim();
        var clientName = PairingNameBox.Text.Trim();
        if (!SunshineApiClient.IsValidPairingPin(pin))
        {
            PairingStatusText.Text = "Enter the four digits shown by the PiuLink client.";
            PairingStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        if (string.IsNullOrWhiteSpace(clientName))
        {
            PairingStatusText.Text = "Enter a name for this client.";
            PairingStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        SetSunshineControlsBusy(isBusy: true);
        PageArtwork.PlayPairing();
        PairingStatusText.Text = "Sending the PIN to Sunshine…";
        PairingStatusText.Foreground = ResourceBrush("MutedBrush");

        try
        {
            var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
            if (credential is null)
            {
                throw new InvalidOperationException("Set up PiuLink secure control before pairing a client.");
            }

            _sunshineCredential = credential;
            await _sunshineApi.SubmitPinAsync(
                pin,
                clientName,
                credential.Username,
                credential.Secret);
            PairingPinBox.Clear();
            PairingStatusText.Text = $"{clientName} was paired successfully.";
            PairingStatusText.Foreground = ResourceBrush("SuccessBrush");
            var overview = await _sunshineApi.GetOverviewAsync(
                credential.Username,
                credential.Secret);
            RenderSunshineOverview(overview);
            await _log.WriteAsync("A Moonlight client was paired through the native Sunshine control page.");
        }
        catch (Exception exception)
        {
            PairingStatusText.Text = DescribeSunshineFailure(exception);
            PairingStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Sunshine pairing failed: {exception.GetType().Name}");
        }
        finally
        {
            SetSunshineControlsBusy(isBusy: false);
        }
    }

    private void RenderSunshineOverview(SunshineOverview overview)
    {
        _sunshineOverview = overview;
        SunshineAppsCountText.Text = overview.ApplicationCount.ToString();
        SunshineClientsCountText.Text = overview.PairedClientCount.ToString();
        if (!overview.HasCurrentApplication)
        {
            SunshineSessionStateText.Text = "Ready";
            SunshineSessionStateText.Foreground = ResourceBrush("SuccessBrush");
            SunshineSessionStatusText.Text = "No application is being held. A client can start any app.";
            EndSunshineSessionButton.Visibility = Visibility.Collapsed;
        }
        else if (overview.HasActiveStreams)
        {
            SunshineSessionStateText.Text = $"Streaming · {overview.ActiveStreamCount}";
            SunshineSessionStateText.Foreground = ResourceBrush("PrimaryBrush");
            SunshineSessionStatusText.Text = "A client is actively streaming. PiuLink will not interrupt it.";
            EndSunshineSessionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SunshineSessionStateText.Text = "Retained";
            SunshineSessionStateText.Foreground = ResourceBrush("WarningBrush");
            SunshineSessionStatusText.Text = "The client disconnected, but Sunshine kept the previous app. End it before switching apps.";
            EndSunshineSessionButton.Visibility = Visibility.Visible;
        }

        EndSunshineSessionButton.IsEnabled = overview.HasRetainedApplication;
        UpdateDisplayControls();
    }

    private async void EndSunshineSession_Click(object sender, RoutedEventArgs e)
    {
        if (_sunshineOverview?.HasRetainedApplication != true)
        {
            SunshineSessionStatusText.Text = "PiuLink will not end a session while a client is connected.";
            SunshineSessionStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null)
        {
            SunshineSessionStatusText.Text = "Set up PiuLink secure control before ending a previous session.";
            SunshineSessionStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        SetSunshineControlsBusy(isBusy: true);
        EndSunshineSessionButton.Content = "Ending…";
        SunshineSessionStatusText.Text = "Closing the retained app without restarting Sunshine…";
        SunshineSessionStatusText.Foreground = ResourceBrush("MutedBrush");

        try
        {
            await _sunshineApi.CloseCurrentApplicationAsync(credential.Username, credential.Secret);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var overview = await GetSunshineOverviewWithRetryAsync(credential);
            RenderSunshineOverview(overview);
            SunshineControlStatusText.Text = "The previous session ended. Sunshine is ready for a new app.";
            SunshineControlStatusText.Foreground = ResourceBrush("SuccessBrush");
            await _log.WriteAsync("Ended a retained Sunshine application through the local native API.");
        }
        catch (Exception exception)
        {
            SunshineSessionStatusText.Text = DescribeSunshineFailure(exception);
            SunshineSessionStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Ending the retained Sunshine application failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            EndSunshineSessionButton.Content = "End previous session";
            SetSunshineControlsBusy(isBusy: false);
        }
    }

    private async Task<SunshineOverview> GetSunshineOverviewWithRetryAsync(
        SunshineDeviceCredential credential)
    {
        var delays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(700),
            TimeSpan.FromMilliseconds(1400)
        };

        for (var attempt = 0; ; attempt++)
        {
            if (delays[attempt] > TimeSpan.Zero)
            {
                await Task.Delay(delays[attempt]);
            }

            try
            {
                return await _sunshineApi.GetOverviewAsync(credential.Username, credential.Secret);
            }
            catch (Exception exception) when (
                attempt < delays.Length - 1 && IsTransientSunshineFailure(exception))
            {
                // Sunshine may still be binding its local HTTPS endpoint after service restart.
            }
        }
    }

    private static bool IsTransientSunshineFailure(Exception exception) =>
        exception is TaskCanceledException ||
        exception is HttpRequestException { StatusCode: null } ||
        exception is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError };

    private static string DescribeExceptionForLog(Exception exception)
    {
        var details = new List<string>(capacity: 3);
        for (var current = exception; current is not null && details.Count < 3; current = current.InnerException)
        {
            var message = current.Message.ReplaceLineEndings(" ").Trim();
            details.Add(string.IsNullOrWhiteSpace(message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {message}");
        }

        return string.Join(" -> ", details);
    }

    private void SetSunshineControlsBusy(bool isBusy)
    {
        var sunshineInstalled = _snapshot is not null &&
                                _snapshot.Sunshine.State != ComponentState.Missing;
        SunshineConnectButton.IsEnabled = !isBusy && sunshineInstalled && !_sunshineAuthenticated;
        UpdateSunshineConnectionAction(isBusy);
        SunshineRefreshButton.IsEnabled = !isBusy && _sunshineAuthenticated;
        SubmitPairingPinButton.IsEnabled = !isBusy && _sunshineAuthenticated;
        EndSunshineSessionButton.IsEnabled = !isBusy &&
                                            _sunshineAuthenticated &&
                                            _sunshineOverview?.HasRetainedApplication == true;
        PairingNameBox.IsEnabled = !isBusy;
        PairingPinBox.IsEnabled = !isBusy;
        var configurationAvailable = _sunshineAuthenticated && !_sunshineConfigurationBusy;
        LoadSunshineConfigurationButton.IsEnabled = configurationAvailable;
        SunshineConfigurationEditor.IsEnabled = configurationAvailable;
        SaveSunshineConfigurationButton.IsEnabled = configurationAvailable &&
                                                     _sunshineConfigurationLoaded &&
                                                     _sunshineOverview?.HasActiveStreams != true;
        UpdateDisplayControls();
    }

    private void UpdateSunshineConnectionAction(bool isBusy)
    {
        var sunshineInstalled = _snapshot is not null &&
                                _snapshot.Sunshine.State != ComponentState.Missing;
        SunshineConnectButton.Content = isBusy
            ? "Connecting…"
            : _sunshineAuthenticated
                ? "Connected"
                : _sunshineCredential is null
                    ? "Set up secure control"
                    : "Repair connection";
        SunshineConnectButton.IsEnabled = !isBusy && sunshineInstalled && !_sunshineAuthenticated;
    }

    private async void LoadTailscaleSettings_Click(object sender, RoutedEventArgs e) =>
        await LoadTailscaleSettingsAsync();

    private async Task LoadTailscaleSettingsAsync()
    {
        if (_tailscaleSettingsBusy ||
            _snapshot?.Tailscale.State == ComponentState.Missing)
        {
            return;
        }

        _tailscaleSettingsBusy = true;
        SetTailscaleSettingsControlsBusy(true);
        TailscaleSettingsStatusText.Text = "Reading Tailscale settings from this PC…";
        TailscaleSettingsStatusText.Foreground = ResourceBrush("PrimaryBrush");
        try
        {
            var preferences = await _tailscaleControl.GetPreferencesAsync();
            TailscaleAcceptRoutesCheckBox.IsChecked = preferences.AcceptRoutes;
            TailscaleAcceptDnsCheckBox.IsChecked = preferences.AcceptDns;
            TailscaleShieldsUpCheckBox.IsChecked = preferences.ShieldsUp;
            TailscaleSshCheckBox.IsChecked = preferences.RunSsh;
            TailscaleWebClientCheckBox.IsChecked = preferences.RunWebClient;
            TailscaleAutoUpdateCheckBox.IsChecked = preferences.AutoUpdate;
            DisconnectTailscaleButton.Content = preferences.WantRunning ? "Disconnect" : "Reconnect";
            TailscaleSettingsStatusText.Text = preferences.ShieldsUp
                ? "Settings loaded. Incoming Tailscale connections are currently blocked."
                : "Settings loaded. This device accepts incoming connections allowed by your tailnet policy.";
            TailscaleSettingsStatusText.Foreground = preferences.ShieldsUp
                ? ResourceBrush("WarningBrush")
                : ResourceBrush("SuccessBrush");
        }
        catch (Exception exception)
        {
            TailscaleSettingsStatusText.Text = $"PiuLink could not read Tailscale settings: {exception.Message}";
            TailscaleSettingsStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Reading Tailscale preferences failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _tailscaleSettingsBusy = false;
            SetTailscaleSettingsControlsBusy(false);
        }
    }

    private async void SaveTailscaleSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_tailscaleSettingsBusy)
        {
            return;
        }

        if (TailscaleShieldsUpCheckBox.IsChecked == true &&
            System.Windows.MessageBox.Show(
                LocalizationService.Instance.TranslateLiteral(
                    "Shields up blocks incoming Tailscale connections, including PiuLink streaming. Save it anyway?"),
                LocalizationService.Instance.TranslateLiteral("PiuLink Tailscale settings"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _tailscaleSettingsBusy = true;
        SetTailscaleSettingsControlsBusy(true);
        TailscaleSettingsStatusText.Text = "Saving Tailscale settings…";
        TailscaleSettingsStatusText.Foreground = ResourceBrush("PrimaryBrush");
        try
        {
            await _tailscaleControl.ApplyPreferencesAsync(new TailscalePreferences(
                TailscaleAcceptRoutesCheckBox.IsChecked == true,
                TailscaleAcceptDnsCheckBox.IsChecked == true,
                TailscaleShieldsUpCheckBox.IsChecked == true,
                TailscaleSshCheckBox.IsChecked == true,
                TailscaleWebClientCheckBox.IsChecked == true,
                TailscaleAutoUpdateCheckBox.IsChecked == true,
                _snapshot?.Tailscale.State == ComponentState.Ready));
            TailscaleSettingsStatusText.Text = "Tailscale settings saved.";
            TailscaleSettingsStatusText.Foreground = ResourceBrush("SuccessBrush");
            await RefreshAsync();
            await _log.WriteAsync("Tailscale preferences were updated through PiuLink.");
        }
        catch (Exception exception)
        {
            TailscaleSettingsStatusText.Text = $"PiuLink could not save Tailscale settings: {exception.Message}";
            TailscaleSettingsStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Saving Tailscale preferences failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _tailscaleSettingsBusy = false;
            SetTailscaleSettingsControlsBusy(false);
        }
    }

    private async void DisconnectTailscale_Click(object sender, RoutedEventArgs e)
    {
        if (_tailscaleSettingsBusy)
        {
            return;
        }

        var reconnect = _snapshot?.Tailscale.State != ComponentState.Ready;
        _tailscaleSettingsBusy = true;
        SetTailscaleSettingsControlsBusy(true);
        TailscaleSettingsStatusText.Text = reconnect
            ? "Reconnecting Tailscale…"
            : "Disconnecting Tailscale…";
        TailscaleSettingsStatusText.Foreground = ResourceBrush("PrimaryBrush");
        try
        {
            await _tailscaleControl.SetRunningAsync(reconnect);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            TailscaleSettingsStatusText.Text = $"Tailscale could not change connection state: {exception.Message}";
            TailscaleSettingsStatusText.Foreground = ResourceBrush("ErrorBrush");
        }
        finally
        {
            _tailscaleSettingsBusy = false;
            SetTailscaleSettingsControlsBusy(false);
            await LoadTailscaleSettingsAsync();
        }
    }

    private async void LogoutTailscale_Click(object sender, RoutedEventArgs e)
    {
        if (_tailscaleSettingsBusy ||
            System.Windows.MessageBox.Show(
                LocalizationService.Instance.TranslateLiteral(
                    "Sign this PC out of Tailscale? Remote clients will lose access until you sign in again."),
                LocalizationService.Instance.TranslateLiteral("Sign out of Tailscale"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _tailscaleSettingsBusy = true;
        SetTailscaleSettingsControlsBusy(true);
        try
        {
            await _tailscaleControl.LogoutAsync();
            await RefreshAsync();
            TailscaleSettingsStatusText.Text = "This PC is signed out of Tailscale.";
            TailscaleSettingsStatusText.Foreground = ResourceBrush("WarningBrush");
        }
        catch (Exception exception)
        {
            TailscaleSettingsStatusText.Text = $"Tailscale sign-out failed: {exception.Message}";
            TailscaleSettingsStatusText.Foreground = ResourceBrush("ErrorBrush");
        }
        finally
        {
            _tailscaleSettingsBusy = false;
            SetTailscaleSettingsControlsBusy(false);
        }
    }

    private void SetTailscaleSettingsControlsBusy(bool busy)
    {
        var installed = _snapshot is not null && _snapshot.Tailscale.State != ComponentState.Missing;
        foreach (var control in new System.Windows.Controls.Control[]
                 {
                     TailscaleAcceptRoutesCheckBox,
                     TailscaleAcceptDnsCheckBox,
                     TailscaleShieldsUpCheckBox,
                     TailscaleSshCheckBox,
                     TailscaleWebClientCheckBox,
                     TailscaleAutoUpdateCheckBox,
                     LoadTailscaleSettingsButton,
                     SaveTailscaleSettingsButton,
                     DisconnectTailscaleButton,
                     LogoutTailscaleButton
                 })
        {
            control.IsEnabled = installed && !busy;
        }
    }

    private async void LoadSunshineConfiguration_Click(object sender, RoutedEventArgs e) =>
        await LoadSunshineConfigurationAsync();

    private async Task LoadSunshineConfigurationAsync()
    {
        if (_sunshineConfigurationBusy || !_sunshineAuthenticated)
        {
            return;
        }

        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null)
        {
            return;
        }

        _sunshineConfigurationBusy = true;
        SetSunshineControlsBusy(isBusy: false);
        SunshineConfigurationStatusText.Text = "Reading the complete Sunshine configuration…";
        SunshineConfigurationStatusText.Foreground = ResourceBrush("PrimaryBrush");
        try
        {
            SunshineConfigurationEditor.Text = await _sunshineApi.GetConfigurationJsonAsync(
                credential.Username,
                credential.Secret);
            _sunshineConfigurationLoaded = true;
            SunshineConfigurationStatusText.Text = "Complete local configuration loaded. Unknown fields will be preserved.";
            SunshineConfigurationStatusText.Foreground = ResourceBrush("SuccessBrush");
        }
        catch (Exception exception)
        {
            _sunshineConfigurationLoaded = false;
            SunshineConfigurationStatusText.Text = DescribeSunshineFailure(exception);
            SunshineConfigurationStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Reading complete Sunshine configuration failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _sunshineConfigurationBusy = false;
            SetSunshineControlsBusy(isBusy: false);
        }
    }

    private async Task RefreshSshTerminalStatusAsync()
    {
        var defaults = SshDefaults.Read();
        SshUsernameBox.Text = defaults.Username;
        SshPortBox.Text = defaults.Port.ToString();
        var status = await SshTerminalSetup.ProbeAsync();
        SshTerminalStatusText.Text = $"OpenSSH：{status.OpenSsh} · Windows PowerShell";
    }

    private async void SaveSshDefaults_Click(object sender, RoutedEventArgs e)
    {
        try { SshDefaults.Save(SshUsernameBox.Text, int.Parse(SshPortBox.Text)); await RefreshSshTerminalStatusAsync(); }
        catch (Exception error) { SshTerminalStatusText.Text = error.Message; }
    }
    private async void EnableOpenSsh_Click(object sender, RoutedEventArgs e) { try { await SshTerminalSetup.SetOpenSshAsync(true); await RefreshSshTerminalStatusAsync(); } catch (Exception error) { SshTerminalStatusText.Text = error.Message; } }
    private async void DisableOpenSsh_Click(object sender, RoutedEventArgs e) { try { await SshTerminalSetup.SetOpenSshAsync(false); await RefreshSshTerminalStatusAsync(); } catch (Exception error) { SshTerminalStatusText.Text = error.Message; } }
    private void RetryGatherWindows_Click(object sender, RoutedEventArgs e) {
        if (_gatherWindowsTarget is null) {
            GatherWindowsStatus.Text = "当前没有集中窗口任务，请从客户端选择物理屏幕。";
            RetryGatherWindowsButton.Visibility = Visibility.Collapsed;
            return;
        }
        _gatherWindowsApplied = false;
        GatherWindowsStatus.Text = "等待确认串流状态后重试…";
        RetryGatherWindowsButton.Visibility = Visibility.Collapsed;
    }

    private async void SaveSunshineConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_sunshineConfigurationBusy ||
            !_sunshineConfigurationLoaded ||
            _sunshineOverview?.HasActiveStreams == true)
        {
            SunshineConfigurationStatusText.Text = _sunshineOverview?.HasActiveStreams == true
                ? "A client is streaming now. End the stream before saving Sunshine settings."
                : "Load the complete configuration before saving it.";
            SunshineConfigurationStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        var credential = _sunshineCredential ?? _sunshineManagedIdentity.TryRead();
        if (credential is null)
        {
            SunshineConfigurationStatusText.Text = "Set up secure Sunshine control first.";
            SunshineConfigurationStatusText.Foreground = ResourceBrush("WarningBrush");
            return;
        }

        if (!await _remoteDisplayApplyGate.WaitAsync(0))
        {
            SunshineConfigurationStatusText.Text = "屏幕正在准备或恢复，请稍后再保存。";
            return;
        }
        _sunshineConfigurationBusy = true;
        SetSunshineControlsBusy(isBusy: false);
        SunshineConfigurationStatusText.Text = "Saving every Sunshine setting and restarting the service…";
        SunshineConfigurationStatusText.Foreground = ResourceBrush("PrimaryBrush");
        try
        {
            if (DisplayRecoveryJournal.Pending) throw new InvalidOperationException("请等当前屏幕事务结束后再保存配置。");
            await _sunshineApi.ApplyIdleConfigurationJsonAsync(
                SunshineConfigurationEditor.Text,
                credential.Username,
                credential.Secret);
            var overview = await GetSunshineOverviewWithRetryAsync(credential);
            RenderSunshineOverview(overview);
            await LoadDisplaySettingsAsync();
            SunshineConfigurationStatusText.Text = "Sunshine settings saved and the service restarted successfully.";
            SunshineConfigurationStatusText.Foreground = ResourceBrush("SuccessBrush");
            await _log.WriteAsync("Complete Sunshine configuration was updated through PiuLink.");
        }
        catch (Exception exception)
        {
            SunshineConfigurationStatusText.Text = DescribeSunshineFailure(exception);
            SunshineConfigurationStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Saving complete Sunshine configuration failed: {DescribeExceptionForLog(exception)}");
        }
        finally
        {
            _sunshineConfigurationBusy = false;
            _remoteDisplayApplyGate.Release();
            SetSunshineControlsBusy(isBusy: false);
        }
    }

    private static string DescribeSunshineFailure(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "Sunshine rejected PiuLink's device credential. Run secure setup again.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "Sunshine refused this local request. Check the administrator account.",
        HttpRequestException { InnerException: IOException } =>
            "Sunshine's local secure connection did not complete. Restart secure control and try again.",
        HttpRequestException =>
            "PiuLink could not reach Sunshine on this PC. Make sure the service is running.",
        TaskCanceledException =>
            "Sunshine did not respond in time. Make sure the service is running.",
        ArgumentException argumentException => argumentException.Message,
        _ => "Sunshine returned an unexpected response. See the local PiuLink log for details."
    };

    private void OpenTailscale_Click(object sender, RoutedEventArgs e)
    {
        NetworkNavButton.IsChecked = true;
    }

    private void DownloadTailscale_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(OfficialLinks.TailscaleWindowsDownload)
        {
            UseShellExecute = true
        });

    private async void CheckTailscaleInstallation_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async void SignInTailscale_Click(object sender, RoutedEventArgs e)
    {
        if (_tailscaleSignInInProgress)
        {
            return;
        }

        _tailscaleSignInInProgress = true;
        TailscaleSignInButton.IsEnabled = false;
        TailscaleSignInButton.Content = UiText("Opening browser…");
        TailscaleSignInStatusText.Text = UiText("Requesting a secure sign-in address from Tailscale…");
        TailscaleSignInStatusText.Foreground = ResourceBrush("PrimaryBrush");

        try
        {
            var result = await _tailscaleControl.ConnectAsync();
            if (result.AuthenticationUri is not null)
            {
                Process.Start(new ProcessStartInfo(result.AuthenticationUri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }

            await RefreshAsync();
            TailscaleSignInStatusText.Text = UiText(result.Detail is null
                ? result.Message
                : $"{result.Message} {result.Detail}");
            TailscaleSignInStatusText.Foreground = result.State switch
            {
                TailscaleConnectState.Connected => ResourceBrush("SuccessBrush"),
                TailscaleConnectState.AwaitingAuthentication => ResourceBrush("PrimaryBrush"),
                _ => ResourceBrush("WarningBrush")
            };
            await _log.WriteAsync($"Tailscale guided sign-in finished with state {result.State}.");
        }
        catch (Exception exception)
        {
            TailscaleSignInStatusText.Text = UiText("PiuLink could not start Tailscale sign-in. Try checking the installation again.");
            TailscaleSignInStatusText.Foreground = ResourceBrush("ErrorBrush");
            await _log.WriteAsync($"Tailscale guided sign-in failed: {exception.GetType().Name}");
        }
        finally
        {
            _tailscaleSignInInProgress = false;
            var connected = _snapshot?.Tailscale.State == ComponentState.Ready;
            var installed = _snapshot is not null &&
                            _snapshot.Tailscale.State != ComponentState.Missing;
            TailscaleSignInButton.IsEnabled = installed && !connected;
            TailscaleSignInButton.Content = UiText(connected
                ? "Connected"
                : "Open sign-in again");
        }

    }

    private void OpenSunshine_Click(object sender, RoutedEventArgs e)
    {
        StreamingNavButton.IsChecked = true;
        SunshineConnectButton.Focus();
    }
}
