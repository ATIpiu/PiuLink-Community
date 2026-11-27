using System.Diagnostics;
using System.IO;
using System.Windows;
using PiuLink.Desktop;

namespace PiuLink.Client;

public partial class LogViewerWindow : Window
{
    private int _revision;
    public LogViewerWindow() { InitializeComponent(); Loaded += async (_, _) => await RefreshAsync(); }
    private async Task RefreshAsync()
    {
        int revision = ++_revision;
        try {
            var text = await Task.Run(ClientLog.ReadRecent);
            if (revision == _revision) LogText.Text = string.IsNullOrWhiteSpace(text) ? "暂时没有日志。" : text;
        } catch (Exception error) { if (revision == _revision) LogText.Text = "读取失败：" + error.Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var result = await DesktopClipboard.CopyAsync(this, LogText.Text);
        if (result != CopyResult.Canceled)
            ((System.Windows.Controls.Button)sender).Content = result == CopyResult.Copied ? "已复制" : "复制失败，点此重试";
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        if (System.Windows.MessageBox.Show(this, "发送连接失败阶段、错误类别和崩溃调用位置到 PiuLink 诊断服务？单次最多 16 KiB。",
                "上传诊断日志", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        button.IsEnabled = false;
        try { System.Windows.MessageBox.Show(this, "报告编号  " + await PiuLink.Diagnostics.DiagnosticUpload.SendAsync(), "上传成功"); }
        catch (Exception error) { System.Windows.MessageBox.Show(this, error.Message, "上传失败"); }
        finally { button.IsEnabled = true; }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ClientLog.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", ClientLog.DirectoryPath) { UseShellExecute = true });
    }
    private async void Clear_Click(object sender, RoutedEventArgs e) { ClientLog.Clear(); await RefreshAsync(); }
}
