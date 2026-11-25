using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PiuLink.Rendezvous;

public sealed record HostInvite(string Room, Guid HostId, string ServiceBase = HostInvite.Base)
{
    public const string Base = "https://atipiu.org:8443/piulink/";
    public string Link => $"{ServiceBase}join#{Room}.{HostId:D}";
    public static string NormalizeService(string value)
    {
        value = value.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || value.IndexOfAny(['?', '#', '\\', '@']) >= 0 ||
            uri.AbsolutePath.TrimEnd('/') is not ("" or "/piulink"))
            throw new FormatException("请填写 HTTPS 服务地址，例如 https://example.org/piulink/。");
        return uri.GetLeftPart(UriPartial.Authority) + "/piulink/";
    }
    internal static readonly HttpClient DirectoryClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 4096 };
    private static readonly HttpClient Direct = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 32768 };

    public static HostInvite? Parse(string input)
    {
        var value = input.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) return null;
        int marker = value.IndexOf("/piulink/join#", StringComparison.Ordinal);
        if (marker < 0) throw new FormatException("请输入完整的 Host 邀请链接。");
        var service = NormalizeService(value[..marker]);
        var authority = value[..marker];
        var fragment = value[(marker + "/piulink/join#".Length)..];
        if (new Uri(authority).AbsolutePath == "/" && fragment.Length == 101 && fragment[64] == '.' &&
            Regex.IsMatch(fragment[..64], "^[0-9a-f]{64}$") && Guid.TryParseExact(fragment[65..], "D", out var id))
            return new(fragment[..64], id, service);
        throw new FormatException("请输入从 PiuLink Host 复制的完整邀请链接。");
    }

    public static HostInvite FromOwner(string owner, Guid id, string serviceBase = Base) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{owner}\n{id:D}"))).ToLowerInvariant(), id, NormalizeService(serviceBase));

    internal HttpRequestMessage Post(string verb, string credential, int version = 1)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{ServiceBase}v{version}/{verb}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return message;
    }

    public static bool IsCandidate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? b[0] is > 0 and < 224 && !(b[0] == 169 && b[1] == 254)
            : ip.ScopeId == 0 && (b[0] >> 5 == 1 || (b[0] & 0xfe) == 0xfc);
    }

    public static async Task<Guid> ReadHostIdAsync(IPAddress ip, CancellationToken token)
    {
        var url = new UriBuilder("http", ip.ToString(), 47989, "serverinfo").Uri;
        var xml = XDocument.Parse(await Direct.GetStringAsync(url, token).ConfigureAwait(false));
        if (xml.Root?.Attribute("status_code")?.Value == "200" &&
            Guid.TryParse(xml.Root.Element("uniqueid")?.Value, out var id)) return id;
        throw new InvalidDataException("地址上的服务没有返回有效的 Sunshine 主机标识。");
    }

    private async Task<IPAddress?> ProbeAsync(IPAddress ip, CancellationToken token)
    {
        try { return await ReadHostIdAsync(ip, token).ConfigureAwait(false) == HostId ? ip : null; }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or
            IOException or InvalidDataException or OperationCanceledException or System.Xml.XmlException) { return null; }
    }

    public async Task<string> ResolveAsync(string? cached, CancellationToken token,
        Func<CancellationToken,Task<string>>? punch = null)
    {
        if (IPAddress.TryParse(cached?.Trim('[', ']'), out var previous) && IsCandidate(previous) &&
            await ProbeAsync(previous, token).ConfigureAwait(false) != null) return previous.ToString();

        using var request = Post("resolve", Room);
        using var response = await DirectoryClient.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("邀请已关闭或 Host 离线。请在 Host 上检查邀请状态。");
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<Presence>(cancellationToken:token).ConfigureAwait(false);
        if (info?.HostId != HostId || info.Addresses is not { Length: > 0 and <= 13 })
            throw new InvalidDataException("邀请与服务返回的 Host 信息不符。");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = info.Addresses.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
            .OfType<IPAddress>().Where(IsCandidate).Distinct().Select(ip => ProbeAsync(ip, stop.Token)).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var task = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(task);
                if (await task.ConfigureAwait(false) is { } ip) return ip.ToString();
            }
        }
        finally { stop.Cancel(); }
        token.ThrowIfCancellationRequested();
        if (punch != null) return await punch(token).ConfigureAwait(false);
        throw new InvalidOperationException("Host 已登记，但当前网络无法直连。可使用同一局域网、虚拟组网或已可达的公网地址。");
    }

    internal sealed record Presence(Guid HostId, string[] Addresses);
}
