using System.Diagnostics;
using System.IO;
using System.Windows;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace PiuLink.ShareTarget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            ShareOperation? operation = null;
            try
            {
                var activation = Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
                if (activation is not ShareTargetActivatedEventArgs share)
                    throw new InvalidOperationException("请从文件右键的“共享”面板选择 PiuLink。");
                operation = share.ShareOperation;
                operation.ReportStarted();
                var paths = new List<string>();
                if (operation.Data.Contains(StandardDataFormats.StorageItems))
                {
                    foreach (var item in await operation.Data.GetStorageItemsAsync())
                    {
                        if (item is not StorageFile file) throw new IOException("请选择文件，文件夹暂不支持直接发送。");
                        if (string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path))
                            throw new IOException("请先将云端文件下载到电脑，再分享给手机。");
                        paths.Add(file.Path);
                    }
                }
                else if (operation.Data.Contains(StandardDataFormats.Text) || operation.Data.Contains(StandardDataFormats.WebLink))
                {
                    var text = operation.Data.Contains(StandardDataFormats.Text)
                        ? await operation.Data.GetTextAsync() : (await operation.Data.GetWebLinkAsync()).AbsoluteUri;
                    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiuLink", "shared-text");
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, $"PiuLink-text-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
                    await File.WriteAllTextAsync(path, text);
                    paths.Add(path);
                }
                if (paths.Count == 0) throw new IOException("分享内容中没有可发送的文件或文本。");
                var host = Path.Combine(AppContext.BaseDirectory, "..", "PiuLink.Host.exe");
                if (!File.Exists(host)) host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ATIPiu Labs", "PiuLink Host", "PiuLink.Host.exe");
                var start = new ProcessStartInfo(Path.GetFullPath(host)) { UseShellExecute = false };
                start.ArgumentList.Add("--send");
                foreach (var path in paths) start.ArgumentList.Add(path);
                using var launched = Process.Start(start) ?? throw new IOException("无法打开 PiuLink Host。");
                operation.ReportCompleted();
            }
            catch (Exception error)
            {
                try { operation?.ReportError(error.Message); } catch { }
                MessageBox.Show(error.Message, "PiuLink 文件分享");
            }
            finally { app.Shutdown(); }
        };
        app.Run();
    }
}
