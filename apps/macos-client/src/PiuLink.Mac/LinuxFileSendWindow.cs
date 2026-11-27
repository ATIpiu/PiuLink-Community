using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace PiuLink.Mac;

internal sealed class LinuxFileSendWindow : Window
{
    internal static string HostTool
    {
        get
        {
            const string packaged = "/usr/bin/piulink-host";
            return File.Exists(packaged) ? packaged : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/piulink-host");
        }
    }
    private sealed record Recipient(string Name, string Fingerprint, bool TransferReady = true)
    {
        internal string Label { get; set; } = Name;
        public override string ToString() => TransferReady ? Label : $"{Label} · 等待授权";
    }

    private sealed record Batch(Recipient Peer, string[] Paths)
    {
        internal int Confirmed { get; set; }
        internal int Remaining => Paths.Length - Confirmed;
    }

    private readonly ComboBox devices = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "暂无可选设备" };
    private readonly TextBlock deviceHint = new() { Classes = { "hint" } };
    private readonly TextBlock headline = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock detail = new() { Classes = { "hint" } };
    private readonly ProgressBar progress = new() { Height = 5, Minimum = 0, IsVisible = false };
    private readonly Button send = new() { Classes = { "connect" } };
    private readonly Button refresh = new() { Content = "刷新" };
    private readonly Button cancel = new() { Content = "停止发送", IsVisible = false };
    private readonly Button discard = new() { Content = "结束这批", IsVisible = false };
    private CancellationTokenSource? operation;
    private Batch? batch;
    private string? lastFolder;
    private bool closed;

    internal LinuxFileSendWindow()
    {
        Title = "发送文件"; Width = 520; Height = 435; MinWidth = 400; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var deviceRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
        deviceRow.Children.Add(devices);
        Grid.SetColumn(refresh, 1); deviceRow.Children.Add(refresh);
        Content = new ScrollViewer { Content = new StackPanel {
            Margin = new Thickness(24), Spacing = 16, Children = {
                new TextBlock { Text = "发送文件", FontSize = 26, FontWeight = FontWeight.SemiBold },
                new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 8, Children = {
                    new TextBlock { Text = "收件设备", FontWeight = FontWeight.SemiBold }, deviceRow, deviceHint
                } } },
                new StackPanel { Spacing = 6, Children = { headline, progress, detail } },
                new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { send, cancel, discard } }
            }
        } };
        devices.SelectionChanged += (_, _) => {
            if (operation == null && batch == null) ClearReport();
            UpdateControls();
        };
        refresh.Click += async (_, _) => await Run(LoadDevices);
        send.Click += async (_, _) => await Run(SendFiles);
        cancel.Click += (_, _) => { cancel.IsEnabled = false; operation?.Cancel(); };
        discard.Click += (_, _) => { batch = null; ClearReport(); UpdateControls(); };
        Opened += async (_, _) => await Run(LoadDevices);
        Closed += (_, _) => { closed = true; operation?.Cancel(); };
    }

    private void ClearReport()
    {
        headline.Text = detail.Text = "";
        headline.IsVisible = detail.IsVisible = progress.IsVisible = false;
    }

    private void Report(string title, string message)
    {
        headline.Text = title; detail.Text = message;
        headline.IsVisible = true; detail.IsVisible = message.Length > 0;
    }

    private void UpdateControls()
    {
        var peer = devices.SelectedItem as Recipient;
        var idle = operation == null;
        devices.IsEnabled = idle && batch == null && devices.ItemCount > 0;
        refresh.IsEnabled = idle;
        send.IsEnabled = idle && peer is { TransferReady: true } && (batch == null || peer.Fingerprint == batch.Peer.Fingerprint);
        send.Content = batch == null ? "选择文件并发送" : $"继续发送剩余 {batch.Remaining} 个";
        send.IsVisible = !cancel.IsVisible;
        discard.IsVisible = batch != null && idle;
        deviceHint.Text = peer switch {
            { TransferReady: true } => "文件授权已就绪。",
            { TransferReady: false } => "在收件设备上打开 PiuLink，点一下这台 Linux 主机，再点「刷新」。",
            _ when batch != null => $"等待原收件设备 {batch.Peer.Name}。重新连接后点「刷新」。",
            _ => "在手机或平板上打开 PiuLink，连接这台 Linux 主机后点「刷新」。"
        };
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (closed || operation != null) return;
        using var current = new CancellationTokenSource();
        operation = current; UpdateControls();
        try { await action(current.Token); }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!closed) Report("已停止发送", BatchStatus());
        }
        catch (Exception error)
        {
            if (!closed) Report(action == LoadDevices ? "无法读取收件设备" : "发送未完成", BatchStatus() + error.Message);
        }
        finally
        {
            operation = null;
            if (!closed) { cancel.IsVisible = false; UpdateControls(); }
        }
    }

    private string BatchStatus() => batch == null ? "" :
        $"{batch.Peer.Name} 已确认 {batch.Confirmed}/{batch.Paths.Length} 个。待确认「{Path.GetFileName(batch.Paths[batch.Confirmed])}」。\n";

    private async Task LoadDevices(CancellationToken token)
    {
        var target = batch?.Peer.Fingerprint ?? (devices.SelectedItem as Recipient)?.Fingerprint;
        devices.ItemsSource = null;
        if (batch == null) ClearReport();
        deviceHint.Text = "正在读取收件设备…";
        var json = await Command(["peers"], token);
        var peers = JsonSerializer.Deserialize<Recipient[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        foreach (var group in peers.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var peer in group)
                peer.Label = $"{peer.Name} · {peer.Fingerprint[..Math.Min(8, peer.Fingerprint.Length)]}";
        devices.ItemsSource = peers;
        devices.SelectedItem = peers.FirstOrDefault(p => p.Fingerprint == target) ??
            (batch == null ? peers.FirstOrDefault(p => p.TransferReady) ?? peers.FirstOrDefault() : null);
        if (batch != null) Report("还有文件待确认", BatchStatus().TrimEnd());
    }

    private async Task SendFiles(CancellationToken token)
    {
        if (devices.SelectedItem is not Recipient { TransferReady: true } peer) return;
        if (batch == null)
        {
            var directory = Directory.Exists(lastFolder) ? lastFolder! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using var folder = await StorageProvider.TryGetFolderFromPathAsync(directory);
            var files = await StorageProvider.OpenFilePickerAsync(new() {
                AllowMultiple = true, Title = "选择要发送的文件", SuggestedStartLocation = folder
            });
            token.ThrowIfCancellationRequested();
            if (files.Count == 0) return;
            var paths = files.Select(file => file.TryGetLocalPath() ?? throw new IOException("请先把文件保存到本机。")).ToArray();
            lastFolder = Path.GetDirectoryName(paths[0]);
            batch = new Batch(peer, paths);
        }
        if (batch.Peer.Fingerprint != peer.Fingerprint) return;
        cancel.IsVisible = cancel.IsEnabled = true; UpdateControls();
        progress.Maximum = batch.Paths.Length; progress.Value = batch.Confirmed; progress.IsVisible = true;
        while (batch.Remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var path = batch.Paths[batch.Confirmed];
            Report($"{batch.Confirmed + 1}/{batch.Paths.Length} · {Path.GetFileName(path)}", $"正在连接 {peer.Name}…");
            await Command(["send", peer.Fingerprint, path], token, line => {
                if (!closed && !line.StartsWith("[1/1]", StringComparison.Ordinal)) detail.Text = line;
            });
            // The Host exits successfully only after the recipient acknowledges this file.
            progress.Value = ++batch.Confirmed;
        }
        Report("对方已确认保存", $"{peer.Name} 已收到全部 {batch.Paths.Length} 个文件。");
        batch = null;
    }

    private static async Task<string> Command(string[] args, CancellationToken token, Action<string>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(HostTool)) throw new IOException("请在本机安装 PiuLink Linux Host，然后刷新。");
        var start = new ProcessStartInfo(HostTool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Linux Host 文件发送工具。");
        using var stop = token.Register(() => {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        string? last = null;
        while (await process.StandardError.ReadLineAsync() is string line) { last = line; progress?.Invoke(line); }
        await process.WaitForExitAsync();
        var output = await stdout;
        if (process.ExitCode != 0) {
            token.ThrowIfCancellationRequested();
            throw new IOException(last ?? "Linux Host 未能完成操作。");
        }
        return output;
    }
}
