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
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ClientLog.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", ClientLog.DirectoryPath) { UseShellExecute = true });
    }
    private async void Clear_Click(object sender, RoutedEventArgs e) { ClientLog.Clear(); await RefreshAsync(); }
}
