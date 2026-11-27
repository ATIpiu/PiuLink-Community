using Microsoft.Win32;
using System.Text.Json;

namespace PiuLink.Host.Services;

internal sealed record PendingDisplaySettings(
    string? DeviceId,
    bool ExclusiveDisplay,
    bool SmartDisplay);

internal static class HostPreferences
{
    private const string RegistryPath = @"Software\ATIPiu Labs\PiuLink Host";
    private const string MoveWindowsValue = "MoveWindowsToVirtualDisplay";
    private const string RecoverWindowsValue = "RecoverWindowsWhenDisplayDisappears";
    private const string SessionGuardianValue = "SessionGuardianEnabled";
    private const string SmartDisplayValue = "SmartDisplayEnabled";
    private const string LanguageValue = "Language";
    private const string AccountClientIdValue = "AccountClientId";
    private const string PendingDisplaySettingsValue = "PendingDisplaySettings";
    private const string PendingDisplayInventoryRefreshValue = "PendingDisplayInventoryRefresh";
    private const string AutoPairLanValue = "AutoPairLan";
    private const string AutoPairTailnetValue = "AutoPairTailnet";
    private const string StartSilentlyValue = "StartSilently";

    public static string Language
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key?.GetValue(LanguageValue) as string ?? "system";
            }
            catch
            {
                return "system";
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue(LanguageValue, value, RegistryValueKind.String);
            }
            catch
            {
                // Keep using the selected language for this session if persistence is unavailable.
            }
        }
    }

    public static string AccountClientId
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                if (key.GetValue(AccountClientIdValue) is string existing &&
                    existing.Length is >= 8 and <= 128)
                {
                    return existing;
                }

                var created = $"windows-{Guid.NewGuid():N}";
                key.SetValue(AccountClientIdValue, created, RegistryValueKind.String);
                return created;
            }
            catch
            {
                return $"windows-{Guid.NewGuid():N}";
            }
        }
    }

    public static bool MoveWindowsToVirtualDisplay
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key?.GetValue(MoveWindowsValue) switch
                {
                    int value => value != 0,
                    string value when bool.TryParse(value, out var parsed) => parsed,
                    _ => true
                };
            }
            catch
            {
                // Preferences must never block the Host from opening. The product default is enabled.
                return true;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue(MoveWindowsValue, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch
            {
                // Keep the in-memory UI choice even if this Windows profile denies registry writes.
            }
        }
    }

    public static bool RecoverWindowsWhenDisplayDisappears
    {
        get => ReadBoolean(RecoverWindowsValue, defaultValue: true);
        set => WriteBoolean(RecoverWindowsValue, value);
    }

    public static bool SessionGuardianEnabled
    {
        get => ReadBoolean(SessionGuardianValue, defaultValue: true);
        set => WriteBoolean(SessionGuardianValue, value);
    }

    public static bool SmartDisplayEnabled
    {
        get => ReadBoolean(SmartDisplayValue, defaultValue: true);
        set => WriteBoolean(SmartDisplayValue, value);
    }

    public static bool AutoPairLan
    {
        get => ReadBoolean(AutoPairLanValue, defaultValue: false);
        set => WriteBoolean(AutoPairLanValue, value);
    }

    public static bool AutoPairTailnet
    {
        get => ReadBoolean(AutoPairTailnetValue, defaultValue: false);
        set => WriteBoolean(AutoPairTailnetValue, value);
    }

    public static bool StartSilently
    {
        get => ReadBoolean(StartSilentlyValue, defaultValue: true);
        set => WriteBoolean(StartSilentlyValue, value);
    }

    public static PendingDisplaySettings? PendingDisplayChange
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                if (key?.GetValue(PendingDisplaySettingsValue) is not string json ||
                    string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                var pending = JsonSerializer.Deserialize<PendingDisplaySettings>(json);
                return pending is not null &&
                       (string.IsNullOrWhiteSpace(pending.DeviceId) || Guid.TryParse(pending.DeviceId, out _))
                    ? pending with { DeviceId = string.IsNullOrWhiteSpace(pending.DeviceId) ? null : pending.DeviceId }
                    : null;
            }
            catch
            {
                return null;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                if (value is null)
                {
                    key.DeleteValue(PendingDisplaySettingsValue, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(
                        PendingDisplaySettingsValue,
                        JsonSerializer.Serialize(value),
                        RegistryValueKind.String);
                }
            }
            catch
            {
                // The MainWindow also retains the pending change for this process.
            }
        }
    }

    public static bool PendingDisplayInventoryRefresh
    {
        get => ReadBoolean(PendingDisplayInventoryRefreshValue, defaultValue: false);
        set => WriteBoolean(PendingDisplayInventoryRefreshValue, value);
    }


    private static bool ReadBoolean(string valueName, bool defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(valueName) switch
            {
                int value => value != 0,
                string value when bool.TryParse(value, out var parsed) => parsed,
                _ => defaultValue
            };
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void WriteBoolean(string valueName, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(valueName, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Keep the current UI choice if this Windows profile denies registry writes.
        }
    }

}
