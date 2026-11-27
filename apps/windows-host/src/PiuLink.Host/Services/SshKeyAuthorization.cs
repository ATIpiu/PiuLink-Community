using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PiuLink.Host.Services;

internal static class SshKeyAuthorization
{
    private const int MaxPreferredLength = -1;
    private const int IncludeIndirectGroups = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupUsersInfo0
    {
        public IntPtr Name;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserGetLocalGroups(string? serverName, string userName, int level,
        int flags, out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public static bool IsAuthorized(string value, string username)
    {
        var key = Validate(value);
        var path = AuthorizedKeysPath(username, out _);
        if (!File.Exists(path)) return false;
        try { return File.ReadLines(path).Any(line => line.Trim().Equals(key, StringComparison.Ordinal)); }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    public static string Validate(string value)
    {
        var key = value.Trim();
        if (!Regex.IsMatch(key, @"\Assh-rsa [A-Za-z0-9+/]+={0,2}(?: [A-Za-z0-9_-]+)?\z") || key.Length > 2048)
            throw new ArgumentException("请粘贴手机“设备授权”中生成的完整 SSH 公钥。");
        var blob = Convert.FromBase64String(key.Split(' ')[1]);
        if (blob.Length < 256 || blob.Length > 1024 || blob.Length < 11 ||
            !blob.AsSpan(0, 11).SequenceEqual(new byte[] { 0, 0, 0, 7, 115, 115, 104, 45, 114, 115, 97 }))
            throw new ArgumentException("SSH 公钥格式无效。");
        return key;
    }

    public static async Task AuthorizeAsync(string value, string username)
    {
        var key = Validate(value);
        var path = AuthorizedKeysPath(username, out var admin);
        if (IsAuthorized(key, username)) return;
        using var identity = WindowsIdentity.GetCurrent();
        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        string principals = admin ? "'S-1-5-32-544','S-1-5-18'" : Quote(identity.User!.Value) + ",'S-1-5-18'";
        // Local UI only. The companion endpoint must obtain explicit desktop consent first.
        string script = "$ErrorActionPreference='Stop'; try { $p=" + Quote(path) + "; $k=" + Quote(key) + "; " +
            "New-Item -ItemType Directory -Force -Path (Split-Path -Parent $p) | Out-Null; " +
            "if (!(Test-Path -LiteralPath $p)) { [IO.File]::WriteAllText($p,'') }; " +
            "$lines=[IO.File]::ReadAllLines($p); if (!($lines | Where-Object { $_.Trim() -eq $k })) { " +
            "[IO.File]::AppendAllText($p,[Environment]::NewLine+$k+[Environment]::NewLine,[Text.Encoding]::ASCII) }; " +
            "$acl=New-Object Security.AccessControl.FileSecurity; $acl.SetAccessRuleProtection($true,$false); " +
            "foreach ($s in @(" + principals + ")) { $sid=New-Object Security.Principal.SecurityIdentifier($s); " +
            "$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow'))) }; " +
            "$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier(" + (admin ? "'S-1-5-32-544'" : Quote(identity.User!.Value)) + "))); " +
            "Set-Acl -LiteralPath $p -AclObject $acl; " +
            "exit 0 } catch { exit 1 }";
        var start = new ProcessStartInfo("powershell.exe") {
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
        };
        if (admin) start.Verb = "runas";
        using var process = Process.Start(start) ?? throw new IOException("无法启动授权工具。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("授权未完成，请检查 OpenSSH 公钥文件权限。");
    }

    private static string AuthorizedKeysPath(string username, out bool admin)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!username.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
            !username.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本地授权仅适用于当前 Windows 账户。其他账户请由其本人添加公钥。");
        admin = IsLocalAdministratorAccount(username, identity);
        return admin ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "administrators_authorized_keys")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");
    }

    internal static bool IsLocalAdministratorAccount(string username, WindowsIdentity? identity = null)
    {
        // A normal desktop process receives a filtered UAC token. Its Administrators SID
        // is deny-only, so token role checks incorrectly classify the account as standard.
        // Query account membership instead, which matches the group test used by sshd.
        var accountName = username.Contains('\\') ? username[(username.LastIndexOf('\\') + 1)..] : username;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var result = NetUserGetLocalGroups(null, accountName, 0, IncludeIndirectGroups,
                out buffer, MaxPreferredLength, out var count, out _);
            if (result == 0)
            {
                var administratorsAccount = (NTAccount)new SecurityIdentifier("S-1-5-32-544")
                    .Translate(typeof(NTAccount));
                var administratorsName = administratorsAccount.Value.Contains('\\')
                    ? administratorsAccount.Value[(administratorsAccount.Value.LastIndexOf('\\') + 1)..]
                    : administratorsAccount.Value;
                var itemSize = Marshal.SizeOf<LocalGroupUsersInfo0>();
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<LocalGroupUsersInfo0>(buffer + index * itemSize);
                    var groupName = Marshal.PtrToStringUni(item.Name);
                    if (string.IsNullOrWhiteSpace(groupName)) continue;
                    if (groupName.Equals(administratorsName, StringComparison.OrdinalIgnoreCase)) return true;
                    try
                    {
                        var sid = (SecurityIdentifier)new NTAccount(groupName)
                            .Translate(typeof(SecurityIdentifier));
                        if (sid.Value == "S-1-5-32-544") return true;
                    }
                    catch (IdentityNotMappedException) { }
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }

        using var ownedIdentity = identity is null ? WindowsIdentity.GetCurrent() : null;
        var effectiveIdentity = identity ?? ownedIdentity!;
        return effectiveIdentity.Groups?.Any(group => group.Value == "S-1-5-32-544") == true;
    }
}
