using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PiuLink.Desktop;

internal static class WindowChrome
{
    internal static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Apply));
    }

    private static void Apply(object sender, RoutedEventArgs args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || SystemParameters.HighContrast) return;
        var handle = new WindowInteropHelper((Window)sender).Handle;
        if (handle == IntPtr.Zero) return;
        const int useImmersiveDarkMode = 20;
        int enabled = 1;
        _ = DwmSetWindowAttribute(handle, useImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
