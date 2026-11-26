using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using PiuLink.Desktop;
using PiuLink.Host.Services;

namespace PiuLink.Host;

public partial class WebAccessWindow : Window
{
    private Process? _process;
    private string? _link, _request;
    private CancellationTokenSource? _lifetime;
    internal WebAccessWindow()
    {
        InitializeComponent();
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 36);
        Detail.Text = "链接可在 10 分钟内使用一次。你允许后，对方可在浏览器查看主显示器并操作键鼠。";
        Closed += (_, _) => Stop();
    }

    private async void StartWeb(object sender, RoutedEventArgs e)
    {
        if (_process != null) return;
        using var lifetime = new CancellationTokenSource();
        using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"piulink-web-host.exe")) {
            RedirectStandardInput=true, RedirectStandardOutput=true, UseShellExecute=false, CreateNoWindow=true,
            StandardInputEncoding=new System.Text.UTF8Encoding(false), StandardOutputEncoding=System.Text.Encoding.UTF8 } };
        var message = "本次网页访问已结束。";
        try {
            var service = InvitePreferences.Service;
            process.Start();
            _process=process; _lifetime=lifetime;
            StartButton.Visibility=Visibility.Collapsed; StopButton.Visibility=Visibility.Visible;
            StateText.Text="正在登记网页访问…"; Detail.Text=new Uri(service).Authority;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {service}));
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line) {
                using var doc=JsonDocument.Parse(line);
                var value=doc.RootElement.GetProperty("value").GetString() ?? "";
                switch (doc.RootElement.GetProperty("type").GetString()) {
                    case "ready":
                        WebIcon.Symbol=HostSymbol.Invitation;
                        _link=value; CopyButton.Visibility=Visibility.Visible; StateText.Text="等待浏览器连接";
                        Detail.Text="将网页链接发给信任的人。对方请求连接后，这里会显示确认码。"; break;
                    case "approval":
                        WebIcon.Symbol=HostSymbol.Shield;
                        if (value.Length!=64) throw new IOException("浏览器请求无效。");
                        _request=value; Code.Text=value[..6].ToUpperInvariant(); Code.Visibility=Visibility.Visible;
                        CopyButton.Visibility=Visibility.Collapsed; AllowButton.Visibility=DenyButton.Visibility=Visibility.Visible;
                        StateText.Text="浏览器请求控制桌面"; Detail.Text="请与对方核对下方确认码。允许后，对方可以操作此电脑。";
                        Fallback.Clear();Fallback.Visibility=Visibility.Collapsed;Activate();DenyButton.Focus();break;
                    case "connected":
                        WebIcon.Symbol=HostSymbol.PrivateNetwork;
                        _link=null; StateText.Text="浏览器已连接 · P2P 直连";Detail.Text="对方可以查看主显示器并操作键鼠。关闭网页访问可立即结束控制。";break;
                    case "error": message=value;break;
                }
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) message=ex.Message; }
        finally {
            Stop();
            if (_process!=null) {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                catch (TimeoutException) { if (!process.HasExited) process.Kill(); }
            }
            _process=null; _lifetime=null; _link=null; _request=null;
            AllowButton.Visibility=DenyButton.Visibility=Code.Visibility=CopyButton.Visibility=StopButton.Visibility=Fallback.Visibility=Visibility.Collapsed;
            Fallback.Clear(); StartButton.Visibility=Visibility.Visible; StopButton.IsEnabled=true;
            WebIcon.Symbol=HostSymbol.Browser;
            StateText.Text="网页访问已关闭";Detail.Text=message;
        }
    }
    private async void Decide(object sender, RoutedEventArgs e)
    {
        if (_process==null || _request==null) return;
        var id=_request;_request=null; AllowButton.Visibility=DenyButton.Visibility=Code.Visibility=Visibility.Collapsed;
        bool allow=sender==AllowButton;
        StateText.Text=allow?"正在建立直连…":"已拒绝请求"; Detail.Text="";
        try {await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {id,allow}));}
        catch (IOException) {Stop();}
        catch (InvalidOperationException) {Stop();}
    }
    private async void CopyWeb(object sender, RoutedEventArgs e)
    {
        var link=_link;
        if (link==null || _lifetime==null) return;
        var result=await DesktopClipboard.CopyAsync(this,link,_lifetime.Token);
        if (_link!=link || !IsVisible || result==CopyResult.Canceled) return;
        if (result==CopyResult.Copied) {Detail.Text="网页链接已复制。请只分享给信任的人。";Fallback.Clear();Fallback.Visibility=Visibility.Collapsed;}
        else {Fallback.Text=link;Fallback.Visibility=Visibility.Visible;Fallback.Focus();Fallback.SelectAll();Detail.Text="剪贴板正被占用，可稍后按 Ctrl+C 复制。";}
    }
    private void StopWeb(object sender,RoutedEventArgs e) {StopButton.IsEnabled=false;Stop();}
    private void Stop() {
        _link=null;_request=null;_lifetime?.Cancel();
        try {_process?.StandardInput.Close();} catch (InvalidOperationException) { }
    }
}
