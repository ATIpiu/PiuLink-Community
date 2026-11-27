using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Storage;
using PiuLink.Client.Core.Streaming;

namespace PiuLink.Mac;

public sealed class MainWindow : Window
{
    private readonly JsonHostRepository repository = new(Path.Combine(DesktopPlatform.Config, "hosts.json"));
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel controls = new() { Spacing = 14 };
    private readonly ComboBox hosts = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox apps = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox name = new() { Watermark = "主机名称" };
    private readonly TextBox address = new() { Watermark = "Host 邀请链接、IP 或 Tailscale 地址" };
    private readonly TextBox engine = new() { Text = DesktopPlatform.FindEngine(), Watermark = "Moonlight 可执行文件路径" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Text = "正在读取主机…" };
    private readonly ComboBox resolution = Options("1920x1080", "1280x720", "2560x1440", "3840x2160");
    private readonly ComboBox fps = Options("60", "30", "120");
    private readonly ComboBox bitrate = Options("20000", "10000", "40000", "80000");
    private readonly CheckBox windowed = new() { Content = "窗口模式", IsChecked = true };
    private readonly Button connect;
    private readonly Button refreshApps;
    private readonly Button pair;
    private readonly Expander manageHosts;
    private List<HostProfile> saved = [];
    private bool loaded;
    private bool busy;
    private bool closed;
    private LinuxFileSendWindow? fileWindow;

    public MainWindow()
    {
        Title = $"PiuLink · {DesktopPlatform.Name}"; Width = 740; Height = 820; MinWidth = 520; MinHeight = 500;
        var root = new StackPanel { Margin = new Thickness(28), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = "连接", FontSize = 28, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock { Classes = { "hint" }, Text = $"PiuLink for {DesktopPlatform.Name} · {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} 预览版" });
        root.Children.Add(controls);

        var manage = new StackPanel { Spacing = 8 };
        manage.Children.Add(name);
        manage.Children.Add(address);
        manage.Children.Add(Row(Action("添加主机", () => AddHost(false)), Action("用邀请链接打洞", () => AddHost(true)), Action("移除所选主机", RemoveHost)));
        manage.Children.Add(Action("断开所选主机的 P2P 连接", async () => {
            await HostEndpoint.DisconnectInviteAsync(SelectedHost().InviteLink); status.Text = "P2P 连接已关闭。"; }));
        manageHosts = new Expander { Header = "管理主机", Content = manage, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        controls.Children.Add(Card("我的电脑", hosts, manageHosts));

        connect = Action("开始串流", Stream);
        connect.Classes.Add("connect");
        refreshApps = Action("刷新应用", RefreshApps);
        pair = Action("首次配对", Pair);
        controls.Children.Add(Card("桌面与应用", apps, Row(connect, refreshApps, pair)));
        status.Classes.Add("hint");
        root.Children.Add(status);
        var updateButton = new Button { Content = "检查更新" };
        PiuLink.Updates.UpdateInfo? offeredUpdate = null;
        var chooseBase = new Button { Content = "选择旧安装包", IsVisible = false };
        string? previousInstaller = null;
        chooseBase.Click += async (_, _) => {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions {
                Title = "选择以前下载的 PiuLink 安装包", AllowMultiple = false
            });
            if (files.Count == 1 && files[0].Path.IsFile) {
                previousInstaller = files[0].Path.LocalPath;
                status.Text = $"将复用 {System.IO.Path.GetFileName(previousInstaller)}。";
            }
        };
        updateButton.Click += async (_, _) => {
            updateButton.IsEnabled = false;
            chooseBase.IsEnabled = false;
            try {
                var channel = PiuLink.Updates.ReleaseDownload.Channel(false);
                if (offeredUpdate == null) {
                    offeredUpdate = await PiuLink.Updates.ReleaseDownload.CheckAsync(channel);
                    status.Text = offeredUpdate == null ? "当前已是最新版本。" : $"发现 {offeredUpdate.Version} 版，可复用本机旧安装包。";
                } else {
                    var prepared = await PiuLink.Updates.ReleaseDownload.PrepareAsync(channel, offeredUpdate, previousInstaller);
                    offeredUpdate = null;
                    previousInstaller = null;
                    status.Text = $"已校验安装包，下载 {prepared.Downloaded / 1048576.0:F1} MiB，复用 {prepared.Reused / 1048576.0:F1} MiB。文件位于 {prepared.Path}";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        System.IO.Path.GetDirectoryName(prepared.Path)!) { UseShellExecute = true });
                }
            } catch (Exception error) { status.Text = "更新未完成：" + error.Message; }
            finally {
                chooseBase.IsVisible = offeredUpdate != null;
                chooseBase.IsEnabled = true;
                updateButton.Content = offeredUpdate == null ? "检查更新" : "下载更新";
                updateButton.IsEnabled = true;
            }
        };
        root.Children.Add(Row(updateButton, chooseBase));
        if (OperatingSystem.IsLinux())
        {
            var fileButton = new Button { Content = "发送文件" };
            var fileHint = new TextBlock { Classes = { "hint" } };
            void CheckLocalHost()
            {
                fileButton.IsEnabled = File.Exists(LinuxFileSendWindow.HostTool);
                fileHint.Text = fileButton.IsEnabled ? "发送到与本机配对的手机或平板。"
                    : "在本机安装 PiuLink Linux Host 后，可向配对的手机或平板发送文件。";
            }
            fileButton.Click += (_, _) =>
            {
                if (fileWindow != null) { fileWindow.Activate(); return; }
                fileWindow = new LinuxFileSendWindow();
                fileWindow.Closed += (_, _) => fileWindow = null;
                fileWindow.Show(this);
            };
            CheckLocalHost();
            Activated += (_, _) => CheckLocalHost();
            root.Children.Add(Card("文件传输", fileHint, fileButton));
        }

        var options = new StackPanel { Spacing = 12 };
        options.Children.Add(Row(Label("分辨率", resolution), Label("帧率", fps), Label("码率 kbps", bitrate)));
        options.Children.Add(windowed);
        options.Children.Add(new TextBlock { Text = "Moonlight 路径", FontWeight = FontWeight.SemiBold });
        options.Children.Add(new TextBlock { Classes = { "hint" }, Text = OperatingSystem.IsLinux()
            ? "自动查找 PATH 中的 moonlight，也可填写 Moonlight Qt 的绝对路径。"
            : "默认使用“应用程序”中的 Moonlight.app。" });
        options.Children.Add(engine);
        options.Children.Add(Action("查看日志", ShowLogs));
        var settings = new Expander { Header = "串流设置", Content = options, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(settings);
        // Engine path and format controls must share the connection's busy state.
        controls.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsEnabledProperty) settings.IsEnabled = controls.IsEnabled;
        };
        Content = new ScrollViewer { Content = root };

        hosts.SelectionChanged += (_, _) =>
        {
            apps.ItemsSource = null; apps.SelectedIndex = -1;
            if (hosts.SelectedIndex >= 0 && hosts.SelectedIndex < saved.Count)
            {
                var host = SelectedHost();
                var recent = host.RecentApplications?.Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('-'))
                    .Distinct(StringComparer.Ordinal).ToArray() ?? [];
                apps.ItemsSource = recent;
                apps.SelectedIndex = recent.Length > 0 ? 0 : -1;
                status.Text = recent.Length > 0 ? "已选中上次使用的应用，可直接开始串流。" : "刷新应用后即可连接。";
                var profile = host.StreamSettings;
                SelectOption(resolution, profile?.Resolution ?? "1920x1080");
                SelectOption(fps, (profile?.FramesPerSecond ?? 60).ToString());
                SelectOption(bitrate, (profile?.BitrateKbps ?? 20000).ToString());
                windowed.IsChecked = profile == null || profile.DisplayMode == "windowed";
            }
            UpdateControls();
        };
        apps.SelectionChanged += (_, _) => UpdateControls();
        Opened += async (_, _) => await Run(async () =>
        {
            await LocalLog.WriteAsync($"{DesktopPlatform.Name} client started");
            saved = (await repository.LoadAsync(lifetime.Token)).ToList();
            loaded = true; RenderHosts(saved.MaxBy(h => h.LastConnectedAt)?.Id);
            manageHosts.IsExpanded = saved.Count == 0;
            if (saved.Count == 0) status.Text = "添加主机后点击“首次配对”。已配对的主机可直接刷新应用。";
        });
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
        UpdateControls();
    }

    private static Border Card(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        foreach (var child in children) stack.Children.Add(child);
        return new Border { Classes = { "card" }, Child = stack };
    }

    private static ComboBox Options(params string[] items)
    {
        var box = new ComboBox { MinWidth = 100 };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = 0;
        return box;
    }
    private static void SelectOption(ComboBox box, string value)
    {
        if (!box.Items.Contains(value)) box.Items.Add(value);
        box.SelectedItem = value;
    }
    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }
    private static StackPanel Label(string text, Control control) => new()
    { Spacing = 6, Children = { new TextBlock { Text = text }, control } };
    private Button Action(string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinHeight = 40 };
        button.Click += async (_, _) => await Run(action);
        return button;
    }
    private void UpdateControls()
    {
        controls.IsEnabled = loaded && !busy;
        bool selected = hosts.SelectedIndex >= 0;
        pair.IsEnabled = refreshApps.IsEnabled = selected;
        connect.IsEnabled = selected && apps.SelectedItem is string;
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { await LocalLog.WriteAsync("Operation failed: " + error); if (!closed) status.Text = "未完成：" + error.Message; }
        finally { busy = false; if (!closed) UpdateControls(); }
    }
    private HostProfile SelectedHost() => hosts.SelectedIndex >= 0 && hosts.SelectedIndex < saved.Count
        ? saved[hosts.SelectedIndex] : throw new InvalidOperationException("请先选择主机。");
    private MoonlightQtCommandService Engine()
    {
        var path = engine.Text?.Trim();
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new IOException("找不到 Moonlight Qt。请安装后在「串流设置」中填写可执行文件的完整路径。");
        return new(path);
    }
    private void RenderHosts(Guid? selected = null)
    {
        hosts.ItemsSource = saved.Select(h => $"{h.Name} · {h.Address}").ToArray();
        hosts.SelectedIndex = selected.HasValue ? saved.FindIndex(h => h.Id == selected) : saved.Count > 0 ? 0 : -1;
    }
    private async Task AddHost(bool useP2p)
    {
        var input = address.Text?.Trim() ?? "";
        var invite = PiuLink.Rendezvous.HostInvite.Parse(input);
        if (useP2p && invite == null) throw new ArgumentException("请粘贴 Host 的完整邀请链接。");
        var endpoint = await HostEndpoint.ResolveAsync(input, token: lifetime.Token, useP2p: useP2p);
        var title = name.Text?.Trim();
        if (string.IsNullOrEmpty(title)) title = IsTunnelEndpoint(endpoint.Host) && invite != null ? $"Host {invite.HostId.ToString("N")[..8]}" : endpoint.Host;
        var existing = saved.FirstOrDefault(h => invite != null && h.InviteLink == invite.Link)
            ?? saved.FirstOrDefault(h => HostEndpoint.Parse(h.Address).MoonlightTarget.Equals(endpoint.MoonlightTarget, StringComparison.OrdinalIgnoreCase) && (invite == null || h.InviteLink == null));
        if (existing != null && invite == null)
            throw new ArgumentException("该地址已经添加，请从列表选择主机。");
        var host = existing == null ? new HostProfile(Guid.NewGuid(), title, endpoint.MoonlightTarget, DateTimeOffset.UtcNow, InviteLink: invite?.Link)
            : existing with { Address = endpoint.MoonlightTarget, InviteLink = invite?.Link };
        var next = (existing == null ? saved.Append(host) : saved.Select(h => h.Id == host.Id ? host : h)).ToList();
        await repository.SaveAsync(next, lifetime.Token);
        saved = next; RenderHosts(host.Id); name.Text = ""; address.Text = "";
        status.Text = "主机已保存。首次使用请配对。";
    }
    private async Task RemoveHost()
    {
        var host = SelectedHost();
        await HostEndpoint.DisconnectInviteAsync(host.InviteLink);
        var next = saved.Where(h => h.Id != host.Id).ToList();
        await repository.SaveAsync(next, lifetime.Token);
        saved = next; RenderHosts(); status.Text = "已移除本机的主机记录。";
    }
    private async Task Pair()
    {
        var endpoint = await ResolveSelectedAsync();
        var service = Engine();
        var pin = RandomNumberGenerator.GetInt32(10000).ToString("D4");
        status.Text = $"配对码 {pin}。在 Windows Host 的配对页输入，或在 Linux Host 执行 piulink-host pin。等待最多 120 秒…";
        var result = await service.PairAsync(endpoint, pin, TimeSpan.FromSeconds(120), lifetime.Token);
        if (!result.IsSuccess) throw new InvalidOperationException("配对失败，请确认主机地址和 PIN。" + result.StandardError);
        await RefreshApps();
    }
    private async Task RefreshApps()
    {
        var endpoint = await ResolveSelectedAsync();
        var selected = apps.SelectedItem as string;
        status.Text = "正在获取应用…";
        var result = await Engine().ListAppsAsync(endpoint, TimeSpan.FromSeconds(20), lifetime.Token);
        if (!result.IsSuccess) throw new InvalidOperationException("获取应用失败，请确认主机在线、已配对且网络可达。");
        var names = MoonlightProcessService.ParseAppList(result.StandardOutput);
        apps.ItemsSource = names;
        apps.SelectedItem = names.FirstOrDefault(n => n == selected) ?? names.FirstOrDefault();
        status.Text = names.Count > 0 ? "选择应用即可连接。" : "主机尚未发布应用，请在 Host 中检查应用设置。";
    }
    private async Task Stream()
    {
        var endpoint = await ResolveSelectedAsync();
        var app = apps.SelectedItem as string ?? throw new InvalidOperationException("请先刷新并选择应用。");
        var host = SelectedHost();
        var profile = (host.StreamSettings ?? StreamProfile.Default) with
        {
            Resolution = (string)resolution.SelectedItem!, FramesPerSecond = int.Parse((string)fps.SelectedItem!),
            BitrateKbps = int.Parse((string)bitrate.SelectedItem!), DisplayMode = windowed.IsChecked == true ? "windowed" :
                host.StreamSettings?.DisplayMode == "borderless" ? "borderless" : "fullscreen"
        };
        profile.Validate();
        if (string.IsNullOrWhiteSpace(app) || app.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException("请选择有效的应用。");
        using var process = Engine().LaunchExternalStream(endpoint, app, profile);
        status.Text = "正在启动 Moonlight…";
        var exit = process.WaitForExitAsync(lifetime.Token);
        if (await Task.WhenAny(exit, Task.Delay(750, lifetime.Token)) == exit) {
            await exit;
            throw new IOException($"Moonlight 启动后立即退出（{process.ExitCode}）。请检查引擎路径、配对和串流参数。");
        }
        status.Text = "Moonlight 正在运行。";
        var next = saved.Select(h => h.Id == host.Id ? h with {
            StreamSettings = profile, RecentApplications = [app], LastConnectedAt = DateTimeOffset.UtcNow, Address = endpoint.MoonlightTarget
        } : h).ToList();
        try { await repository.SaveAsync(next, lifetime.Token); saved = next; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = "Moonlight 正在运行。本次串流设置未能保存。";
            await LocalLog.WriteAsync("Could not save stream preferences: " + error.Message);
        }
        await exit;
        status.Text = process.ExitCode == 0 ? "串流已结束。" : "串流未正常结束，请检查主机和网络后重试。";
    }

    private Task<HostEndpoint> ResolveSelectedAsync()
    {
        var host = SelectedHost();
        return HostEndpoint.ResolveAsync(host.InviteLink ?? host.Address, host.Address, lifetime.Token,
            useP2p: host.InviteLink != null && IsTunnelEndpoint(host.Address));
    }

    private static bool IsTunnelEndpoint(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && System.Net.IPAddress.IsLoopback(ip);

    private Task ShowLogs()
    {
        var text = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(LocalLog.ReadAll()) ? "暂时没有日志。" : LocalLog.ReadAll(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily.Parse("Menlo, monospace")
        };
        var refresh = new Button { Content = "刷新" };
        refresh.Click += (_, _) => text.Text = LocalLog.ReadAll();
        var clear = new Button { Content = "清空" };
        clear.Click += (_, _) => { LocalLog.Clear(); text.Text = "暂时没有日志。"; };
        var window = new Window
        {
            Title = "PiuLink 日志", Width = 860, Height = 600,
            Content = new Grid
            {
                Margin = new Thickness(20), RowDefinitions = RowDefinitions.Parse("Auto,*"),
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { refresh, clear } },
                    text
                }
            }
        };
        Grid.SetRow(text, 1);
        window.Show(this);
        return Task.CompletedTask;
    }
}
