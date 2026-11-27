using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;

namespace PiuLink.Client.Core.Streaming;

public sealed record GameStreamHostInfo(string AppVersion, string? GfeVersion, int HttpsPort, int CodecSupport, int CurrentGame, bool IsPaired, string? MacAddress, bool SupportsPiuLinkControl);
public sealed record GameStreamLaunch(string Address, string AppVersion, string? GfeVersion, string? SessionUrl,
    int CodecSupport, byte[] RemoteInputKey, byte[] RemoteInputIv);

public sealed class GameStreamHttpClient
{
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PreparedRequests = new();
    private readonly HostEndpoint _endpoint;
    private readonly GameStreamIdentity _identity;
    private X509Certificate2? _serverCertificate;
    public GameStreamHttpClient(HostEndpoint endpoint, GameStreamIdentity identity) { _endpoint = endpoint; _identity = identity; LoadServerCertificate(); }
    private string ServerCertPath => Path.Combine(_identity.Directory, Sanitize(_endpoint.Host) + "-server.cer");

    public HttpClient CreateCompanionClient()
    {
        EnsureCertificate();
        var pinned = _serverCertificate!;
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ClientCertificates.Add(_identity.Certificate);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null &&
            CryptographicOperations.FixedTimeEquals(certificate.RawData, pinned.RawData);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<string> GetPublicServerInfoAsync(CancellationToken token) => await GetAsync(false, _endpoint.Port, "serverinfo", null, token);
    public static GameStreamHostInfo ParseServerInfo(string xml) => new(
        Value(xml, "appversion"), Optional(xml, "GfeVersion"), int.TryParse(Optional(xml, "HttpsPort"), out var hp) ? hp : 47984,
        int.TryParse(Optional(xml, "ServerCodecModeSupport"), out var codecs) ? codecs : 1,
        int.TryParse(Optional(xml, "currentgame"), out var game) ? game : 0, Optional(xml, "PairStatus") == "1",
        Optional(xml, "mac") is { } mac && mac != "00:00:00:00:00:00" ? mac : null,
        Optional(xml, "piulink_control") == "1");

    public async Task<GameStreamHostInfo> GetHostInfoAsync(CancellationToken token) =>
        ParseServerInfo(await GetPublicServerInfoAsync(token));

    public async Task PairAsync(string pin, Func<CancellationToken, Task> approvePin, CancellationToken token)
    {
        var infoXml = await GetPublicServerInfoAsync(token);
        int major = int.Parse(Value(infoXml, "appversion").Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        bool piuLinkAutoPair = Optional(infoXml, "PiuLinkAutoPair") == "1";
        var autoPairTicket = Optional(infoXml, "PiuLinkAutoPairTicket");
        var salt = RandomNumberGenerator.GetBytes(16);
        var keyMaterial = salt.Concat(Encoding.UTF8.GetBytes(pin)).ToArray();
        var aesKey = (major >= 7 ? SHA256.HashData(keyMaterial) : SHA1.HashData(keyMaterial))[..16];
        var stage1Args = "devicename=roth&updateState=1&phrase=getservercert&salt=" +
            Convert.ToHexString(salt) + "&clientcert=" + Convert.ToHexString(_identity.PemCertificate) +
            (piuLinkAutoPair && !string.IsNullOrWhiteSpace(autoPairTicket)
                ? "&piulinkautopairticket=" + Uri.EscapeDataString(autoPairTicket)
                : "");
        var stage1Task = GetAsync(false, _endpoint.Port, "pair", stage1Args, token);
        if (!piuLinkAutoPair)
        {
            await Task.Delay(350, token);
            await approvePin(token);
        }
        var stage1 = await stage1Task;
        EnsurePaired(stage1, 1);
        var serverPem = Convert.FromHexString(Value(stage1, "plaincert"));
        _serverCertificate = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(serverPem));

        var randomChallenge = RandomNumberGenerator.GetBytes(16);
        var stage2 = await PairCommand("clientchallenge=" + Convert.ToHexString(AesEcb(randomChallenge, aesKey, true)), false, token);
        EnsurePaired(stage2, 2);
        var response = AesEcb(Convert.FromHexString(Value(stage2, "challengeresponse")), aesKey, false);
        int hashLength = major >= 7 ? 32 : 20;
        var serverResponse = response[..hashLength];
        var serverChallenge = response[hashLength..(hashLength + 16)];
        var clientSecret = RandomNumberGenerator.GetBytes(16);
        var clientCertSignature = CertificateSignature(_identity.Certificate);
        var challengeHash = Hash(major, serverChallenge.Concat(clientCertSignature).Concat(clientSecret).ToArray());
        Array.Resize(ref challengeHash, 32);
        var stage3 = await PairCommand("serverchallengeresp=" + Convert.ToHexString(AesEcb(challengeHash, aesKey, true)), false, token);
        EnsurePaired(stage3, 3);
        var secretAndSignature = Convert.FromHexString(Value(stage3, "pairingsecret"));
        var serverSecret = secretAndSignature[..16];
        var serverSignature = secretAndSignature[16..];
        using var serverKey = _serverCertificate.GetRSAPublicKey() ?? throw new CryptographicException("Host pairing key is not RSA.");
        if (!serverKey.VerifyData(serverSecret, serverSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("Host pairing signature is invalid.");
        var expected = Hash(major, randomChallenge.Concat(CertificateSignature(_serverCertificate)).Concat(serverSecret).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(expected, serverResponse)) throw new InvalidOperationException("Incorrect pairing PIN.");
        using var clientKey = _identity.Certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Client key is missing.");
        var signedSecret = clientSecret.Concat(clientKey.SignData(clientSecret, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ToArray();
        EnsurePaired(await PairCommand("clientpairingsecret=" + Convert.ToHexString(signedSecret), false, token), 4);
        EnsurePaired(await PairCommand("phrase=pairchallenge", true, token), 5);
        // Persist only completed pairing; never leave a partially paired host certificate on disk.
        var temporary = ServerCertPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, _serverCertificate.Export(X509ContentType.Cert));
            File.Move(temporary, ServerCertPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<(int Id, string Name)>> GetAppsAsync(CancellationToken token)
    {
        var xml = await GetRawAppListAsync(token);
        var doc = XDocument.Parse(xml);
        return doc.Descendants("App").Select(a => ((int?)a.Element("ID") ?? 0, (string?)a.Element("AppTitle") ?? ""))
            .Where(a => a.Item1 != 0 && a.Item2.Length != 0).ToArray();
    }

    public async Task<string> GetRawAppListAsync(CancellationToken token)
    {
        EnsureCertificate();
        return await GetAsync(true, (await HostInfo(token)).HttpsPort, "applist", null, token);
    }

    public Task<GameStreamLaunch> LaunchDesktopAsync(int width, int height, int fps, CancellationToken token) =>
        LaunchAppAsync("Desktop", width, height, fps, false, token);

    public async Task<GameStreamLaunch> LaunchAppAsync(string appName, int width, int height, int fps, bool enableHdr, CancellationToken token)
    {
        EnsureCertificate();
        var info = await HostInfo(token);
        var apps = await GetAppsAsync(token);
        var app = apps.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
        if (app.Id == 0) throw new InvalidOperationException($"Sunshine did not advertise {appName}.");
        var key = RandomNumberGenerator.GetBytes(16); var iv = new byte[16]; RandomNumberGenerator.Fill(iv.AsSpan(0, 4));
        var hdr = enableHdr ? "&hdrMode=1&clientHdrCapVersion=0&clientHdrCapSupportedFlagsInUint32=0&clientHdrCapMetaDataId=NV_STATIC_METADATA_TYPE_1&clientHdrCapDisplayData=0x0x0x0x0x0x0x0x0x0x0" : "";
        var query = $"appid={app.Id}&mode={width}x{height}x{fps}&additionalStates=1&sops=1&rikey={Convert.ToHexString(key)}&rikeyid={System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(iv.AsSpan(0, 4))}{hdr}&localAudioPlayMode=0&surroundAudioInfo=196610&remoteControllersBitmap=1&gcmap=1&gcpersist=1&piulinkPrepared={PreparedRequests.GetValueOrDefault(_endpoint.Host, "")}";
        var verb = info.CurrentGame == app.Id ? "resume" : "launch";
        var xml = await GetAsync(true, info.HttpsPort, verb, query, token);
        var success = Optional(xml, verb == "launch" ? "gamesession" : "resume");
        if (success is null || success == "0") throw new InvalidOperationException($"Sunshine {verb} rejected the {appName} session.");
        return new(_endpoint.Host, info.AppVersion, info.GfeVersion, Optional(xml, "sessionUrl0"), info.CodecSupport, key, iv);
    }

    public async Task<bool> QuitAppAsync(CancellationToken token)
    {
        EnsureCertificate();
        var info = await HostInfo(token);
        var xml = await GetAsync(true, info.HttpsPort, "cancel", null, token);
        return IsQuitAccepted(xml);
    }

    internal static bool IsQuitAccepted(string xml) => Optional(xml, "cancel") == "1";

    internal static void EnsureXmlStatusOk(string body)
    {
        var doc = XDocument.Parse(body);
        var status = doc.Root?.Attribute("status_code")?.Value;
        if (status is not null && status != "200")
            throw new IOException($"Sunshine returned {status}: {doc.Root?.Attribute("status_message")?.Value}");
    }

    private async Task<GameStreamHostInfo> HostInfo(CancellationToken token) => ParseServerInfo(await GetPublicServerInfoAsync(token));
    private async Task<string> PairCommand(string args, bool tls, CancellationToken token) =>
        await GetAsync(tls, tls ? (await HostInfo(token)).HttpsPort : _endpoint.Port, "pair", "devicename=roth&updateState=1&" + args, token);
    private async Task<string> GetAsync(bool tls, int port, string command, string? args, CancellationToken token)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ClientCertificates.Add(_identity.Certificate);
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => !tls || (cert is not null && _serverCertificate is not null && CryptographicOperations.FixedTimeEquals(cert.RawData, _serverCertificate.RawData));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(command == "pair" ? 30 : 8) };
        var query = $"uniqueid={Uri.EscapeDataString(_identity.UniqueId)}&uuid={Guid.NewGuid():N}" + (args is null ? "" : "&" + args);
        using var response = await client.GetAsync(new UriBuilder(tls ? "https" : "http", _endpoint.Host, port, command) { Query = query }.Uri, token);
        var body = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        EnsureXmlStatusOk(body);
        return body;
    }
    private void LoadServerCertificate() { if (File.Exists(ServerCertPath)) _serverCertificate = new X509Certificate2(File.ReadAllBytes(ServerCertPath)); }
    private void EnsureCertificate() { if (_serverCertificate is null) throw new InvalidOperationException("Host is not paired with this PiuLink identity."); }
    private static string Sanitize(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
    private static string Value(string xml, string name) => Optional(xml, name) ?? throw new IOException($"Sunshine response is missing {name}.");
    private static string? Optional(string xml, string name) => XDocument.Parse(xml).Descendants(name).FirstOrDefault()?.Value;
    private static void EnsurePaired(string xml, int stage)
    {
        var paired = Optional(xml, "paired");
        if (paired == "1") return;
        var document = XDocument.Parse(xml);
        var fields = string.Join(",", document.Descendants().Select(element => element.Name.LocalName).Distinct().Take(12));
        throw new IOException(paired is null
            ? $"Sunshine pairing response is missing paired at stage {stage} (fields: {fields})."
            : $"Pairing failed at stage {stage} (paired={paired}).");
    }
    private static byte[] Hash(int major, byte[] data) => major >= 7 ? SHA256.HashData(data) : SHA1.HashData(data);
    private static byte[] AesEcb(byte[] data, byte[] key, bool encrypt) { using var aes = Aes.Create(); aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.Zeros; aes.Key = key; using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor(); return transform.TransformFinalBlock(data, 0, data.Length); }
    private static byte[] CertificateSignature(X509Certificate2 certificate) { var outer = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence(); outer.ReadEncodedValue(); outer.ReadEncodedValue(); return outer.ReadBitString(out _); }
}
