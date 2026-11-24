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
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_log.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", _log.DirectoryPath) { UseShellExecute = true });
    }
    private async void Clear_Click(object sender, RoutedEventArgs e) { _log.Clear(); await _log.WriteAsync("Logs cleared by user."); Refresh(); }
}
