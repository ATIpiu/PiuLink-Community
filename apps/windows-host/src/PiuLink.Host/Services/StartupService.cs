using Microsoft.Win32;

namespace PiuLink.Host.Services;

internal static class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PiuLink Host";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled, bool silent)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Windows did not report the PiuLink executable path.");
        var command = $"\"{executable}\"" + (silent ? " --silent" : string.Empty);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
