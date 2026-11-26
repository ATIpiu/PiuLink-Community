using System.Windows;
using PiuLink.Host.Core.Diagnostics;
using PiuLink.Host.Localization;
using PiuLink.Host.Services;

namespace PiuLink.Host;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--repair-desktop")) {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await DisplayRecoveryJournal.RepairDesktopAsync(); Shutdown(0); }
            catch (Exception error) { using var log = new LocalLog(); await log.WriteAsync(error.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--save-setup-audio" or "--restore-setup-audio" && Guid.TryParse(e.Args[1], out var transaction)) {
            try { MicrophoneSetupDefaults.InUserSession(e.Args[0] == "--restore-setup-audio", transaction); Shutdown(0); }
            catch (Exception) { Shutdown(1); }
            return;
        }
        Guid product = default;
        if ((e.Args.Length == 1 && e.Args[0] == "--install-virtual-microphone") ||
            (e.Args.Length == 2 && Guid.TryParse(e.Args[1], out product) &&
                e.Args[0] is "--msi-install-microphone" or "--msi-rollback-microphone" or "--msi-commit-microphone")) {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try {
                var action = e.Args[0];
                if (action == "--msi-commit-microphone") VirtualMicrophoneDevice.FinishSetup(product);
                else if (action == "--msi-rollback-microphone") VirtualMicrophoneDevice.RollbackSetup(product);
                else {
                    if (action == "--msi-install-microphone") VirtualMicrophoneDevice.BeginSetup(product);
                    await MicrophoneSetupDefaults.InstallAsync(async () => {
                        if (!VirtualMicrophoneDevice.Install()) throw new System.IO.IOException("Bundled microphone driver package is missing.");
                        // Session 0 has no user's audio graph. PnP binding is already verified above.
                        if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0) {
                            if (VirtualMicrophoneDevice.WaitForEndpoints()) return;
                        } else for (int attempt = 0; attempt < 60; attempt++) {
                            if (RemoteMicrophoneOutput.HasDedicatedPair()) return;
                            await Task.Delay(250);
                        }
                        throw new System.IO.IOException("Windows did not enumerate the microphone endpoints.");
                    });
                }
                Shutdown(0); return;
            } catch (Exception error) {
                using var log = new LocalLog();
                await log.WriteAsync("Virtual microphone installation failed: " + error.Message);
            }
            Shutdown(1); return;
        }
        if (e.Args.Contains("--display-recovery-agent")) {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await DisplayRecoveryJournal.RunGuardianAsync();
            Shutdown(); return;
        }
        PiuLink.Diagnostics.DiagnosticUpload.Initialize(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATIPiu Labs", "PiuLink Host", "logs"), "windows-host");
        if (DisplayRecoveryJournal.Pending) DisplayRecoveryJournal.EnsureGuardian();
        // The Host deliberately changes the active Windows display topology while an
        // exclusive virtual desktop is in use.  WPF's hardware composition target can
        // remain black after that Direct3D device/display transition even though the
        // window and dispatcher are still healthy.  This UI is a lightweight control
        // panel, so software composition is the safer choice and avoids a stale black
        // client area without affecting the separately rendered Sunshine stream.
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            System.Windows.Interop.RenderMode.SoftwareOnly;

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var sending = e.Args.Length > 1 && e.Args[0] == "--send";
        if (sending) FileSendEntry.Enqueue(e.Args.Skip(1).ToArray());
        byte launchCommand = sending ? (byte)3 : (byte)1;
        try
        {
            _instance = new SingleInstanceService();
            if (!_instance.IsPrimary)
            {
                if (!await _instance.ActivateExistingAsync(launchCommand, 0))
                    System.Windows.MessageBox.Show("PiuLink 已在运行，但暂时无法唤回窗口。请从系统托盘打开；如果它运行在其他 Windows 会话，请切回该会话。", "PiuLink");
                Shutdown();
                return;
            }
            _instance.Listen((command, appId) => Dispatcher.BeginInvoke(new Action(() => {
                ActivateExistingWindow();
                if (command == 3 && MainWindow is PiuLink.Host.MainWindow senderWindow)
                {
                    var files = FileSendEntry.Drain();
                    if (files.Length > 0) senderWindow.SendFilesToPhone(files);
                }
            })));
        }
        catch (UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show("PiuLink 已在其他权限或 Windows 用户会话中运行，请返回原会话打开。", "PiuLink");
            Shutdown();
            return;
        }
        PiuLink.Desktop.WindowChrome.Register();
        LocalizationService.Instance.SetLanguage(HostPreferences.Language);
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        FileSendEntry.Register();
        if (sending) { var files = FileSendEntry.Drain(); if (files.Length > 0) window.SendFilesToPhone(files); }
        if (e.Args.Any(argument => string.Equals(argument, "--silent", StringComparison.OrdinalIgnoreCase)))
            window.HideOnStartup();
    }

    private void ActivateExistingWindow()
    {
        if (MainWindow is PiuLink.Host.MainWindow host)
            host.ShowFromTray();
        else if (MainWindow is { } window)
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

}
