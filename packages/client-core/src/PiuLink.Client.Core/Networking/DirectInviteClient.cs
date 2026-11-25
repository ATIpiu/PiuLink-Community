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
    private sealed record PathSession(Process Process, IPAddress Address, HostInvite Invite);
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly Dictionary<Guid, PathSession> Sessions = new();

    private static async Task<string> RequestFieldAsync(HostInvite invite, string verb, object? body, string field, CancellationToken token)
    {
        using var message = invite.Post(verb, invite.Room, 2);
        if (body != null) message.Content = JsonContent.Create(body);
        using var response = await HostInvite.DirectoryClient.SendAsync(message,token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("Host 未开启 P2P 打洞或已离线。");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        return json.RootElement.GetProperty(field).GetString() ?? throw new IOException("P2P 服务响应不完整。");
    }

    internal static async Task<string?> ActiveAddressAsync(HostInvite invite, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try {
            return Sessions.TryGetValue(invite.HostId, out var session) &&
                session.Invite == invite && !session.Process.HasExited ? session.Address.ToString() : null;
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> ConnectAsync(HostInvite invite, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Sessions.TryGetValue(invite.HostId, out var active) && !active.Process.HasExited) {
                if (active.Invite == invite) return active.Address.ToString();
                throw new IOException("此 Host 的另一条邀请仍在使用，请先断开该 P2P 会话。");
            }
            if (OperatingSystem.IsMacOS() && Sessions.Values.Any(s => !s.Process.HasExited))
                throw new IOException("另一台 Host 正在使用本机 P2P 端口，请先断开该连接。");

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
                // Darwin only binds 127.0.0.1 without a separately configured loopback alias.
                var address = OperatingSystem.IsMacOS() ? IPAddress.Loopback :
                    IPAddress.Parse($"127.{1+tag[0]%254}.{tag[1]}.{1+tag[2]%254}");
                var lease = new {serviceBase=invite.ServiceBase,leaseId=id,ticket,transportKey=Convert.ToBase64String(secret).TrimEnd('=').Replace('+','-').Replace('/','_'),
                    sourceClientId="source",hostClientId="host"};
                var config = JsonSerializer.Serialize(new {role="source",clientId="source",listenHost=address.ToString(),lease});
                var session = await StartAsync(config,address,invite,token).ConfigureAwait(false);
                Sessions[invite.HostId] = session;
                _ = WatchAsync(invite.HostId,session);
                return address.ToString();
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        finally { Gate.Release(); }
    }

    internal static async Task DisconnectAsync(HostInvite invite)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try {
            if (Sessions.TryGetValue(invite.HostId, out var active) && active.Invite == invite) {
                Sessions.Remove(invite.HostId);
                active.Process.StandardInput.Close();
                try { await active.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException) { if (!active.Process.HasExited) active.Process.Kill(); }
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<PathSession> StartAsync(string config, IPAddress address, HostInvite invite, CancellationToken token)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "piulink-direct.exe" : "piulink-direct");
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
                throw new IOException("未能建立 P2P 直连：" + (ready.RootElement.TryGetProperty("message", out var error) ? error.GetString() : "传输进程未就绪"));
            if (await HostInvite.ReadHostIdAsync(address,startup.Token).ConfigureAwait(false) != invite.HostId)
                throw new IOException("P2P 通道连接了不同的 Host。");
            return new PathSession(process,address,invite);
        }
        catch { Close(process); throw; }
    }

    private static async Task WatchAsync(Guid id, PathSession session)
    {
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                // The transport owns liveness. A quiet app list or paused desktop can be reused.
                if (doc.RootElement.GetProperty("type").GetString() == "error") break;
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
