using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using PiuLink.Rendezvous;

namespace PiuLink.Host.Services;

internal sealed record PastInvite(string Link, DateTimeOffset Created);

internal static class InvitePreferences
{
    private const string Key = @"Software\ATIPiu Labs\PiuLink Host";
    internal static string Service
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return HostInvite.NormalizeService(key?.GetValue("InviteService") as string ?? HostInvite.Base); }
        set { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue("InviteService", HostInvite.NormalizeService(value)); }
    }
    internal static List<PastInvite> History()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        if (key?.GetValue("InviteHistory") is not byte[] bytes) return [];
        return JsonSerializer.Deserialize<List<PastInvite>>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)) ?? [];
    }
    internal static void Remember(string link)
    {
        var items = History();
        if (items.Any(i => i.Link == link)) return;
        items.Insert(0, new(link, DateTimeOffset.Now));
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("InviteHistory", ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(items.Take(20)), null,
            DataProtectionScope.CurrentUser), RegistryValueKind.Binary);
    }
    internal static void ClearHistory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, true);
        key?.DeleteValue("InviteHistory", false);
    }
}
