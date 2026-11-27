using System.Diagnostics;
using System.IO;
using System.Windows;
using PiuLink.Desktop;
using PiuLink.Host.Localization;
using PiuLink.Host.Core.Diagnostics;

namespace PiuLink.Host;

public partial class LogViewerWindow : Window
{
    private readonly LocalLog _log;
    public LogViewerWindow(LocalLog log) { _log = log; InitializeComponent(); Refresh(); }
    private void Refresh() { var text = _log.ReadAll(); LogText.Text = string.IsNullOrWhiteSpace(text) ? "No logs have been recorded yet." : text; }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var result = await DesktopClipboard.CopyAsync(this, LogText.Text);
        if (result != CopyResult.Canceled)
            ((System.Windows.Controls.Button)sender).Content = LocalizationService.Instance.TranslateLiteral(
                result == CopyResult.Copied ? "Copied" : "Copy failed. Retry");
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
        Directory.CreateDirectory(_log.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", _log.DirectoryPath) { UseShellExecute = true });
    }
    private async void Clear_Click(object sender, RoutedEventArgs e) { _log.Clear(); await _log.WriteAsync("Logs cleared by user."); Refresh(); }
}
