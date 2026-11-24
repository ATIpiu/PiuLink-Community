using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PiuLink.Host.Core.Diagnostics;

namespace PiuLink.Host.Services;

internal static class PairedDisplaySecurity
{
    private static string ConfigDirectory => Path.Combine(Path.GetDirectoryName(
        ExecutableLocator.FindSunshine() ?? throw new IOException("Streaming engine is missing."))!, "config");

    public static X509Certificate2 LoadServerCertificate()
    {
        using var pem = X509Certificate2.CreateFromPemFile(
            Path.Combine(ConfigDirectory, "credentials", "cacert.pem"),
            Path.Combine(ConfigDirectory, "credentials", "cakey.pem"));
        return new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
    }

    public static bool IsPaired(X509Certificate? peer)
    {
        if (peer is null) return false;
        var peerHash = Convert.ToHexString(SHA256.HashData(peer.GetRawCertData()));
        return ReadPairedFingerprints()?.Contains(peerHash) == true;
    }

    public static HashSet<string>? ReadPairedFingerprints()
    {
        try
        {
            var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, "sunshine_state.json")));
            foreach (var device in state.RootElement.GetProperty("root").GetProperty("named_devices").EnumerateArray())
            {
                if (device.TryGetProperty("enabled", out var enabled) &&
                    (enabled.ValueKind == JsonValueKind.False || enabled.ToString() == "false" || enabled.ToString() == "0")) continue;
                if (!device.TryGetProperty("cert", out var certProperty) || certProperty.ValueKind != JsonValueKind.String) continue;
                var pem = certProperty.GetString();
                if (string.IsNullOrWhiteSpace(pem)) continue;
                try
                {
                    using var trusted = X509Certificate2.CreateFromPem(pem);
                    fingerprints.Add(Convert.ToHexString(SHA256.HashData(trusted.RawData)));
                }
                catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException) { }
            }
            return fingerprints;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
