using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Dialogs;
using Avalonia.Markup.Xaml;

namespace PiuLink.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Xvfb and some remote desktops share a D-Bus session with the physical display.
        // Keep the file chooser in the same display as the sending window.
        if (OperatingSystem.IsLinux()) builder.UseManagedSystemDialogs();
        builder.StartWithClassicDesktopLifetime(args);
    }
}

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
