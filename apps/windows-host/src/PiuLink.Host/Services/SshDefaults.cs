using Microsoft.Win32;

namespace PiuLink.Host.Services;

internal sealed record SshConnectionDefaults(string Username, int Port, string Platform = "windows");

internal static class SshDefaults
{
    private const string Path = @"Software\ATIPiu Labs\PiuLink Host\SSH";
    public static SshConnectionDefaults Read()
    {
        var username = Environment.UserDomainName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? Environment.UserName : $"{Environment.UserDomainName}\\{Environment.UserName}";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path);
            var stored = key?.GetValue("Username") as string;
            var port = key?.GetValue("Port") is int value && value is >= 1 and <= 65535 ? value : 22;
            return new(string.IsNullOrWhiteSpace(stored) ? username : stored.Trim(), port);
        }
        catch { return new(username, 22); }
    }
    public static void Save(string username, int port)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl) || port is < 1 or > 65535)
            throw new ArgumentException("Enter a valid SSH username and a port from 1 to 65535.");
        using var key = Registry.CurrentUser.CreateSubKey(Path);
        key.SetValue("Username", username.Trim());
        key.SetValue("Port", port, RegistryValueKind.DWord);
    }
}
