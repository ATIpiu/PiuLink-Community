using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiuLink.Host.Linux;

internal sealed class SshApproval(HostState state)
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private string PendingPath => Path.Combine(state.DirectoryPath, "ssh-pending.json");
    private string ApprovedPath(string id) => Path.Combine(state.DirectoryPath, "ssh-approved-" + id);
    internal sealed record Request(string Id, string Device, string Address, string Fingerprint, string Key, DateTimeOffset ExpiresAt);

    internal Request? Pending()
    {
        if (!File.Exists(PendingPath)) return null;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllBytes(PendingPath), HostState.Json);
            return request?.ExpiresAt > DateTimeOffset.UtcNow ? request : null;
        }
        catch (FileNotFoundException) { return null; }
    }

    internal async Task<bool> RequestAsync(string key, string device, string address, CancellationToken shutdown)
    {
        if (device.Length is < 1 or > 80 || device.Any(char.IsControl) ||
            key.Length is < 32 or > 2048 ||
            !Regex.IsMatch(key, @"\A(?:ssh-rsa|ssh-ed25519) [A-Za-z0-9+/]+={0,2}(?: [A-Za-z0-9_-]{1,80})?\z"))
            throw new ArgumentException("Invalid SSH key or device name.");
        if (!_slot.Wait(0)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            timeout.CancelAfter(TimeSpan.FromSeconds(105));
            var token = timeout.Token;
            var id = Guid.NewGuid().ToString("N");
            var fingerprint = await FingerprintAsync(key, token);
            var request = new Request(id, device, address, fingerprint, key, DateTimeOffset.UtcNow.AddSeconds(104));
            HostState.WritePrivate(PendingPath, JsonSerializer.SerializeToUtf8Bytes(request, HostState.Json));
            try
            {
                while (!File.Exists(ApprovedPath(id))) await Task.Delay(300, token);
                return true;
            }
            catch (OperationCanceledException) when (!shutdown.IsCancellationRequested) { return false; }
            finally
            {
                File.Delete(PendingPath);
                File.Delete(ApprovedPath(id));
            }
        }
        finally { _slot.Release(); }
    }

    internal async Task ApproveAsync(string id, string fingerprint, CancellationToken token)
    {
        var request = Pending();
        if (request is null || request.Id != id || request.Fingerprint != fingerprint ||
            await FingerprintAsync(request.Key, token) != fingerprint)
            throw new IOException("The pending SSH request or fingerprint does not match.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "authorized_keys");
        var previous = File.Exists(path) ? File.ReadAllText(path) : "";
        if (!previous.Split('\n').Any(line => line.Trim() == request.Key))
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Add this key manually because authorized_keys is a symbolic link.");
            using var file = new FileStream(path, new FileStreamOptions
            {
                Access = FileAccess.Write, Mode = FileMode.Append, Share = FileShare.Read,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            var separator = previous.Length == 0 || previous.EndsWith('\n') ? "" : "\n";
            file.Write(Encoding.ASCII.GetBytes(separator + request.Key + "\n"));
            file.Flush(flushToDisk: true);
        }
        HostState.WritePrivate(ApprovedPath(id), []);
    }

    private static async Task<string> FingerprintAsync(string key, CancellationToken token)
    {
        var start = new ProcessStartInfo("ssh-keygen", "-l -E sha256 -f -")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not run ssh-keygen.");
        try
        {
            await process.StandardInput.WriteLineAsync(key.AsMemory(), token);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await error;
            var parts = (await output).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (process.ExitCode != 0 || parts.Length < 2 || !parts[1].StartsWith("SHA256:", StringComparison.Ordinal))
                throw new ArgumentException("Invalid OpenSSH public key.");
            return parts[1];
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
}
