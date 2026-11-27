using Microsoft.Win32;
using System.Security.Cryptography;
using System.Net;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PiuLink.Host.Services;

internal sealed class FileTransferPairingStore(string registryPath = @"Software\ATIPiu Labs\PiuLink Host")
{
    internal sealed record Peer(string DeviceId, string Name, string Address, string ProtectedToken, string[]? Addresses = null, string? CertificateFingerprint = null);
    private const string ValueName = "FileTransferDevices";
    private static readonly object Gate = new();

    public List<Peer> Peers()
    {
        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            try
            {
                return key?.GetValue("FileTransferPeers") is string json
                    ? (JsonSerializer.Deserialize<List<Peer>>(json) ?? [])
                        .Where(peer => peer is not null && ValidDeviceId(peer.DeviceId) &&
                            !string.IsNullOrWhiteSpace(peer.Address) && !string.IsNullOrWhiteSpace(peer.ProtectedToken)).ToList()
                    : [];
            }
            catch (JsonException error) { throw new IOException("已保存的文件传输设备列表无法读取。", error); }
        }
    }

    // FileTransferPeers is the authoritative, atomically replaced record. Old hashes are
    // read only when migrating a previously unbound device.
    public string? Bind(FileTransferPairRequest request, bool approveLegacy = false)
    {
        if (!ValidDeviceId(request.DeviceId) || request.CertificateFingerprint.Length != 64 ||
            !request.CertificateFingerprint.All(char.IsAsciiHexDigit) || !IPAddress.TryParse(request.RemoteAddress, out var ip))
            throw new ArgumentException("Invalid file-transfer identity.");
        lock (Gate)
        {
            var peers = Peers();
            var bound = peers.SingleOrDefault(p => p.CertificateFingerprint == request.CertificateFingerprint);
            var legacy = peers.SingleOrDefault(p => p.CertificateFingerprint is null && p.DeviceId == request.DeviceId);
            var previous = bound ?? legacy;
            var alreadyBoundId = peers.Any(p => p.CertificateFingerprint is not null && p.DeviceId == request.DeviceId);
            var legacyHash = bound is null && legacy is null && !alreadyBoundId && Read().TryGetValue(request.DeviceId, out var hash) ? hash : null;
            if (bound is null && (legacy is not null || legacyHash is not null) && !approveLegacy &&
                (string.IsNullOrEmpty(request.Token) || !(legacy is not null
                    ? MatchesToken(legacy, request.Token)
                    : CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(legacyHash!), Hash(request.Token)))))
                return null;

            var address = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
            var addresses = new[] { address }.Concat(previous?.Addresses ?? []).Append(previous?.Address ?? "")
                .Where(a => IPAddress.TryParse(a, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            // Rotate on the first authenticated migration. Legacy tokens have travelled over unencrypted links.
            var token = bound is null ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : Token(bound);
            var protectedToken = bound?.ProtectedToken ?? Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
            if (previous is not null) peers.Remove(previous);
            peers.Add(new(request.DeviceId, request.DeviceName, address, protectedToken, addresses, request.CertificateFingerprint));
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            key.SetValue("FileTransferPeers", JsonSerializer.Serialize(peers), RegistryValueKind.String);
            return token;
        }
    }

    public static string Token(Peer peer) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(peer.ProtectedToken), null, DataProtectionScope.CurrentUser));

    private static bool MatchesToken(Peer peer, string token) =>
        CryptographicOperations.FixedTimeEquals(Hash(Token(peer)), Hash(token));

    public bool Validate(string deviceId, string token, string certificateFingerprint) =>
        Peers().Any(p => p.CertificateFingerprint == certificateFingerprint && p.DeviceId == deviceId && MatchesToken(p, token));

    private static bool ValidDeviceId(string? value) => value is not null && value.Length is >= 8 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    private Dictionary<string, string> Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return key?.GetValue(ValueName) is string json
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []
                : [];
        }
        catch (JsonException error) { throw new IOException("旧文件传输授权无法读取。", error); }
    }
}
