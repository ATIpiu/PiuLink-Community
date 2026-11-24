using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PiuLink.Client.Core.Streaming;
using PiuLink.Rendezvous;

namespace PiuLink.Client.Core.Networking;

internal static class DirectInviteClient
{
    private sealed record PathSession(Process Process, IPAddress Address);
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly Dictionary<Guid, PathSession> Sessions = new();

    private static async Task<string> RequestFieldAsync(HostInvite invite, string verb, object? body, string field, CancellationToken token)
    {
        using var message = HostInvite.Post(verb, invite.Room, 2);
        if (body != null) message.Content = JsonContent.Create(body);
        using var response = await HostInvite.DirectoryClient.SendAsync(message,token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("Host 未开启 P2P 打洞或已离线。");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        return json.RootElement.GetProperty(field).GetString() ?? throw new IOException("P2P 服务响应不完整。");
    }

    public static async Task<string> ConnectAsync(HostInvite invite, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Sessions.TryGetValue(invite.HostId, out var active) && !active.Process.HasExited)
                return active.Address.ToString();

            var hostId = invite.HostId.ToString("D");
            var (identity, pin) = QtCompanionIdentity.ReadIdentity(hostId, false);
            using var certificate = identity;
            var der = Convert.FromBase64String(await RequestFieldAsync(invite,"identity",null,"cert",token).ConfigureAwait(false));
            if (pin != null && !CryptographicOperations.FixedTimeEquals(der,pin))
                throw new IOException("P2P 服务返回的 Host 证书与已保存的配对不符。");
            using var peer = new X509Certificate2(der);
            using var rsa = peer.GetRSAPublicKey() ?? throw new IOException("Host 邀请证书无效。");
            using var signing = identity.GetRSAPrivateKey() ?? throw new IOException("客户端身份缺少签名密钥。");
            var secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                var id = Guid.NewGuid().ToString("N");
                var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var wrapped = Convert.ToBase64String(rsa.Encrypt(secret,RSAEncryptionPadding.OaepSHA256));
                var signed = Encoding.ASCII.GetBytes($"piulink-punch-v2\n{invite.Room}\n{id}\n{time}\n{wrapped}");
                var offer = new {id,time,wrapped,cert=Convert.ToBase64String(identity.RawData),
                    signature=Convert.ToBase64String(signing.SignData(signed,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))};
                var ticket = await RequestFieldAsync(invite,"open",offer,"ticket",token).ConfigureAwait(false);
                var tag = SHA256.HashData(Encoding.ASCII.GetBytes(hostId));
                var address = IPAddress.Parse($"127.{1+tag[0]%254}.{tag[1]}.{1+tag[2]%254}");
                var lease = new {leaseId=id,ticket,transportKey=Convert.ToBase64String(secret).TrimEnd('=').Replace('+','-').Replace('/','_'),
                    sourceClientId="source",hostClientId="host"};
                var config = JsonSerializer.Serialize(new {role="source",clientId="source",listenHost=address.ToString(),lease});
                var session = await StartAsync(config,address,invite.HostId,token).ConfigureAwait(false);
                Sessions[invite.HostId] = session;
                _ = WatchAsync(invite.HostId,session);
                return address.ToString();
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        finally { Gate.Release(); }
    }

    private static async Task<PathSession> StartAsync(string config, IPAddress address, Guid hostId, CancellationToken token)
    {
        var exe = Path.Combine(AppContext.BaseDirectory,"piulink-direct.exe");
        var process = Process.Start(new ProcessStartInfo(exe) {RedirectStandardInput=true,RedirectStandardOutput=true,
            UseShellExecute=false,CreateNoWindow=true}) ?? throw new IOException("无法启动 P2P 传输服务。");
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(35));
            await process.StandardInput.WriteLineAsync(config.AsMemory(),startup.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(startup.Token).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(startup.Token).ConfigureAwait(false)
                ?? throw new IOException("P2P 传输服务已退出。");
            using var ready = JsonDocument.Parse(line);
            if (ready.RootElement.GetProperty("type").GetString() != "ready")
                throw new IOException("未能建立 P2P 直连。请检查两端网络及 Host 的新设备确认提示。");
            if (await HostInvite.ReadHostIdAsync(address,startup.Token).ConfigureAwait(false) != hostId)
                throw new IOException("P2P 通道连接了不同的 Host。");
            return new PathSession(process,address);
        }
        catch { Close(process); throw; }
    }

    private static async Task WatchAsync(Guid id, PathSession session)
    {
        long previous = 0;
        var idle = Stopwatch.StartNew();
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("stats",out var stats)) continue;
                var bytes = stats.GetProperty("UDPBytesSent").GetInt64() + stats.GetProperty("UDPBytesRecv").GetInt64();
                if (bytes != previous) { previous = bytes; idle.Restart(); }
                if (idle.Elapsed > TimeSpan.FromMinutes(3)) break;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
        finally
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { if (Sessions.GetValueOrDefault(id) == session) Sessions.Remove(id); Close(session.Process); }
            finally { Gate.Release(); }
        }
    }

    private static void Close(Process process)
    {
        try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}
