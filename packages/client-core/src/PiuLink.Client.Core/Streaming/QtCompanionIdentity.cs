using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace PiuLink.Client.Core.Streaming;

/// <summary>Use the same locally stored identity and pinned Host certificate as the packaged Qt streamer.</summary>
internal static class QtCompanionIdentity
{
    public static async Task<HttpClient> CreateClientAsync(Networking.HostEndpoint endpoint, CancellationToken token)
    {
        using var discovery = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await discovery.GetAsync(new UriBuilder("http", endpoint.Host, endpoint.Port, "serverinfo").Uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0) {
            if (data.Length + count > 32768) throw new IOException("Host discovery response is too large.");
            data.Write(buffer, 0, count);
        }
        var document = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(data.ToArray()));
        var hostId = document.Descendants("uniqueid").FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(hostId)) throw new IOException("Host did not advertise a device identity.");
        // Discovery is only a lookup hint. The resulting TLS connection must prove the stored certificate.
        return CreateClient(hostId);
    }

    private static HttpClient CreateClient(string hostId)
    {
        var (certificate, pin) = ReadIdentity(hostId, true);
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ClientCertificates.Add(certificate);
        handler.ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && pin != null &&
            CryptographicOperations.FixedTimeEquals(peer.RawData, pin);
        return new PairedClient(handler, certificate) { Timeout = TimeSpan.FromSeconds(25) };
    }

    internal static (X509Certificate2 Certificate, byte[]? Pin) ReadIdentity(string hostId, bool requirePair)
    {
        if (!OperatingSystem.IsWindows()) return UnixQtIdentity.Read(hostId, requirePair);
        var location = new MoonlightQtEngineLocator().Locate();
        if (location.ExecutablePath != null && File.Exists(Path.Combine(Path.GetDirectoryName(location.ExecutablePath)!, "portable.dat")))
            throw new IOException("便携 Qt 配置不受当前客户端支持，请使用完整安装包。");
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Moonlight Game Streaming Project\Moonlight");
        using var hosts = settings?.OpenSubKey("hostsbackup") ?? settings?.OpenSubKey("hosts");
        var pinnedHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in hosts?.GetSubKeyNames() ?? []) {
            using var entry = hosts!.OpenSubKey(name)!;
            var uuid = entry.GetValue("uuid") as string;
            if (!string.Equals(uuid, hostId, StringComparison.OrdinalIgnoreCase)) continue;
            var pem = Pem(entry.GetValue("srvcert"));
            if (!string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(pem))
            {
                if (pinnedHosts.TryGetValue(uuid, out var existingPem) && !string.Equals(existingPem, pem, StringComparison.Ordinal))
                    throw new IOException("该 Host 存在冲突的证书记录，请清理并重新配对。");
                pinnedHosts[uuid] = pem;
            }
        }
        if (requirePair && pinnedHosts.Count != 1) throw new IOException("该地址未绑定到唯一的已配对 Host，请刷新并重新配对。");
        using var pinned = pinnedHosts.Count == 1 ? X509Certificate2.CreateFromPem(pinnedHosts.Values.Single()) : null;
        var pemKey = Pem(settings?.GetValue("key"));
        var pemCert = Pem(settings?.GetValue("certificate"));
        if (string.IsNullOrEmpty(pemKey) || string.IsNullOrEmpty(pemCert)) {
            if (requirePair || pinned != null) throw new IOException("未找到已配对设备的身份密钥。");
            using var fresh = GameStreamIdentity.LoadOrCreate();
            return (new X509Certificate2(fresh.Certificate.Export(X509ContentType.Pkcs12)), null);
        }
        using var identity = X509Certificate2.CreateFromPem(pemCert, pemKey);
        var certificate = new X509Certificate2(identity.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.UserKeySet);
        return (certificate, pinned?.RawData);
    }

    private static string Pem(object? value)
    {
        var text = value is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : value as string ?? "";
        return text.StartsWith("@ByteArray(", StringComparison.Ordinal) && text.EndsWith(')') ? text[11..^1] : text;
    }

    private sealed class PairedClient(HttpMessageHandler handler, X509Certificate2 certificate) : HttpClient(handler)
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) certificate.Dispose(); }
    }
}
