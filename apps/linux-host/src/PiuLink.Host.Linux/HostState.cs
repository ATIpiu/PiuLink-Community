using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PiuLink.Host.Services;

namespace PiuLink.Host.Linux;

internal sealed class HostState(string directory, string sunshineDirectory)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal string DirectoryPath { get; } = directory;
    internal string Inbox { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PiuLink");
    internal int SshPort { get; } = int.TryParse(Environment.GetEnvironmentVariable("PIULINK_SSH_PORT"), out var port)
        && port is > 0 and <= 65535 ? port : 22;
    private string PeerPath => Path.Combine(DirectoryPath, "transfer-peers.json");
    private readonly object _peerLock = new();
    internal sealed record Peer(string DeviceId, string Name, string Fingerprint, string[] Addresses, string Token);
    internal sealed record Recipient(string Name, string Fingerprint, string[] Addresses, bool TransferReady);

    internal X509Certificate2 LoadCertificate() => X509Certificate2.CreateFromPemFile(
        Path.Combine(sunshineDirectory, "credentials", "cacert.pem"),
        Path.Combine(sunshineDirectory, "credentials", "cakey.pem"));

    internal bool IsPaired(X509Certificate? certificate) =>
        certificate is not null && IsPaired(FileTransferTls.Fingerprint(certificate));

    private static Dictionary<string, string> PairedDevices(string sunshineDirectory)
    {
        var paired = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(sunshineDirectory, "sunshine_state.json")));
            if (!state.RootElement.GetProperty("root").TryGetProperty("named_devices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array) return paired;
            foreach (var device in devices.EnumerateArray())
            {
                if (device.TryGetProperty("enabled", out var enabled) &&
                    (enabled.ToString().Equals("false", StringComparison.OrdinalIgnoreCase) || enabled.ToString() == "0")) continue;
                try
                {
                    using var trusted = X509Certificate2.CreateFromPem(device.GetProperty("cert").GetString()!);
                    paired[FileTransferTls.Fingerprint(trusted)] = device.TryGetProperty("name", out var name)
                        ? name.GetString() ?? "PiuLink client" : "PiuLink client";
                }
                catch (Exception error) when (error is CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                {
                    continue;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException) { paired.Clear(); }
        return paired;
    }

    internal bool IsPaired(string fingerprint) => PairedDevices(sunshineDirectory).ContainsKey(fingerprint);

    internal Recipient[] Recipients()
    {
        var peers = ReadPeers().ToDictionary(peer => peer.Fingerprint);
        return PairedDevices(sunshineDirectory).Select(pair =>
        {
            peers.TryGetValue(pair.Key, out var peer);
            return new Recipient(peer?.Name ?? pair.Value, pair.Key, peer?.Addresses ?? [],
                peer is { Token.Length: >= 32, Addresses.Length: > 0 });
        }).OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal Recipient ResolveRecipient(string selector)
    {
        var recipients = Recipients();
        var exact = recipients.FirstOrDefault(peer => peer.Fingerprint.Equals(selector, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var named = recipients.Where(peer => peer.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length switch
        {
            1 => named[0],
            0 => throw new IOException("找不到这台已配对设备。运行 piulink-host peers 查看名称，首次使用请先配对。"),
            _ => throw new IOException("有多台同名设备，请从 piulink-host peers 复制目标设备的完整指纹。")
        };
    }

    internal Peer[] ReadPeers() => File.Exists(PeerPath)
        ? JsonSerializer.Deserialize<Peer[]>(File.ReadAllBytes(PeerPath), Json) ?? []
        : [];

    internal bool ValidateTransfer(string deviceId, string token, string fingerprint) =>
        ReadPeers().Any(peer => peer.Fingerprint == fingerprint && peer.DeviceId == deviceId && EqualToken(peer.Token, token));

    internal FileTransferPairResponse PairTransfer(string deviceId, string name, string fingerprint, IPAddress address)
    {
        if (deviceId.Length is < 8 or > 128 || !deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("Invalid transfer identity.");
        lock (_peerLock)
        {
            var peers = ReadPeers();
            var previous = peers.SingleOrDefault(peer => peer.Fingerprint == fingerprint);
            var token = previous?.Token ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var observedAddress = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
            var addresses = new[] { observedAddress }.Concat(previous?.Addresses ?? []).Distinct().Take(8).ToArray();
            var peer = new Peer(deviceId, name, fingerprint, addresses, token);
            if (previous is null || previous.DeviceId != deviceId || previous.Name != name ||
                !previous.Addresses.SequenceEqual(addresses))
                WritePrivate(PeerPath, JsonSerializer.SerializeToUtf8Bytes(
                    peers.Where(item => item.Fingerprint != fingerprint).Append(peer).ToArray(), Json));
            return new("ready", token, FileTransferServer.Port);
        }
    }

    internal static void WritePrivate(string path, ReadOnlySpan<byte> data)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Access = FileAccess.Write, Mode = FileMode.CreateNew,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal object NetworkStatus() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            network.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown)
        .Select(network => new
        {
            name = network.Name,
            mac = network.GetPhysicalAddress().ToString(),
            addresses = network.GetIPProperties().UnicastAddresses.Select(ip => ip.Address.ToString()).ToArray()
        }).ToArray();

    private static bool EqualToken(string first, string second) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(first)), SHA256.HashData(Encoding.UTF8.GetBytes(second)));
}
