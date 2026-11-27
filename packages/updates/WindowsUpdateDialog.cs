using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace PiuLink.Updates;

internal static class WindowsUpdateDialog
{
    internal static async Task ShowAsync(Window owner, Button button, bool host)
    {
        string label = (string)button.Content;
        button.IsEnabled = false;
        try
        {
            button.Content = "正在检查…";
            var channel = ReleaseDownload.Channel(host);
            var info = await ReleaseDownload.CheckAsync(channel);
            if (info == null) { MessageBox.Show(owner, "当前已是最新版本。", "软件更新"); return; }
            if (MessageBox.Show(owner, $"{channel.Installed} → {info.Version}。完整安装包 {info.Bytes / 1048576.0:F2} MiB。找到旧包时只下载所需内容。现在开始？", "软件更新", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            button.Content = "正在准备安装包…";
            var file = await ReleaseDownload.PrepareAsync(channel, info);
            if (MessageBox.Show(owner, $"已校验安装包，下载 {file.Downloaded / 1048576.0:F2} MiB，复用 {file.Reused / 1048576.0:F2} MiB。现在打开安装程序？", "软件更新", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "更新未完成"); }
        finally { button.Content = label; button.IsEnabled = true; }
    }
}
