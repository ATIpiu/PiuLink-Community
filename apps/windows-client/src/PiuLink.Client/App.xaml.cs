namespace PiuLink.Client;

public partial class App : System.Windows.Application
{
    public App()
    {
        PiuLink.Diagnostics.DiagnosticUpload.Initialize(ClientLog.DirectoryPath, "windows-client");
        PiuLink.Desktop.UpdateRestart.Register("--restarted-after-update");
        PiuLink.Desktop.WindowChrome.Register();
        DispatcherUnhandledException += (_, args) =>
        {
            ClientLog.ErrorSync("app.ui_unhandled", "app", "Unhandled UI error", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ClientLog.ErrorSync("app.process_unhandled", "app", "Unhandled process error", args.ExceptionObject as Exception);
        };
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        ClientLog.Drain();
        base.OnExit(e);
    }
}
