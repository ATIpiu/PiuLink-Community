using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using PiuLink.Host.Services;

namespace PiuLink.Host.Linux;

internal sealed class Companion(HostState state)
{
    private const int Port = 48011;
    private readonly SemaphoreSlim _connections = new(24, 24);
    private readonly SshApproval _ssh = new(state);

    internal async Task RunAsync(CancellationToken shutdown)
    {
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(shutdown);
                if (_connections.Wait(0)) _ = ServeAsync(client, shutdown);
                else client.Dispose();
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken shutdown)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var token = timeout.Token;
            try
            {
                var prefix = new byte[1];
                if (await client.Client.ReceiveAsync(prefix, SocketFlags.Peek, token) == 0) return;
                var network = client.GetStream();
                using var certificate = prefix[0] == 22 ? state.LoadCertificate() : null;
                using var tls = certificate is null ? null : FileTransferTls.Create(network, null, state.IsPaired);
                if (tls is not null) await FileTransferTls.AcceptAsync(tls, certificate!, token);
                var stream = (Stream?)tls ?? network;
                var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                var peer = tls?.RemoteCertificate;
                var owner = peer is not null && state.IsPaired(peer) ? FileTransferTls.Fingerprint(peer) : null;
                await RouteAsync(stream, address, owner, token, shutdown);
            }
            catch (Exception error) when (error is IOException or SocketException or AuthenticationException or
                OperationCanceledException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException) { }
            finally { _connections.Release(); }
        }
    }

    private async Task RouteAsync(Stream stream, IPAddress address, string? owner, CancellationToken token, CancellationToken shutdown)
    {
        try
        {
            var request = await ReadLineAsync(stream, 2048, token);
            var parts = request.Split(' ');
            if (parts.Length != 3 || parts[2] is not ("HTTP/1.1" or "HTTP/1.0")) return;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var remaining = 8192;
            while (true)
            {
                var line = await ReadLineAsync(stream, remaining, token);
                if (line.Length == 0) break;
                remaining -= line.Length + 2;
                var separator = line.IndexOf(':');
                if (separator < 1 || !headers.TryAdd(line[..separator], line[(separator + 1)..].Trim()))
                    throw new InvalidDataException();
            }
            var bodyLength = 0;
            if (headers.ContainsKey("Transfer-Encoding") ||
                (headers.TryGetValue("Content-Length", out var text) && (!int.TryParse(text, out bodyLength) || bodyLength is < 0 or > 8192)))
                throw new InvalidDataException();

            if (parts[0] == "GET" && parts[1] == "/v1/ssh" && (owner is not null || IsLocal(address)))
            {
                await ReplyAsync(stream, 200, new { username = Environment.UserName, port = state.SshPort, platform = "linux" }, token);
                return;
            }
            if (parts[0] == "POST" && parts[1] == "/v1/ssh/authorize" && (owner is not null || IsLocal(address)))
            {
                using var requestBody = await ReadBodyAsync(stream, headers, token);
                var body = requestBody.RootElement;
                var approved = await _ssh.RequestAsync(ReadString(body, "publicKey") ?? "",
                    ReadString(body, "deviceName") ?? "Android", address.ToString(), shutdown);
                using var reply = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                reply.CancelAfter(TimeSpan.FromSeconds(5));
                if (approved)
                    await ReplyAsync(stream, 200, new { status = "authorized", username = Environment.UserName, port = state.SshPort, platform = "linux" }, reply.Token);
                else
                    await ReplyAsync(stream, 403, new { error = "authorization_denied" }, reply.Token);
                return;
            }
            if (owner is null)
            {
                if (bodyLength > 0) await stream.ReadExactlyAsync(new byte[bodyLength], token);
                await ReplyAsync(stream, 403, new { error = "paired_tls_required" }, token);
                return;
            }

            switch (parts[0] + " " + parts[1])
            {
                case "GET /v1/displays":
                {
                    var display = await X11Display.ReadAsync(token);
                    if (display is null) { await ReplyAsync(stream, 503, new { error = "display_unavailable" }, token); break; }
                    var mode = new { display.Width, display.Height };
                    await ReplyAsync(stream, 200, new
                    {
                        exclusiveDisplay = false,
                        supportsSessionDisplays = false, supportsWindowGathering = false, supportsDisplayScaling = false,
                        virtualResolutions = display.IsVirtual ? new[] { mode } : [],
                        virtualRefreshRates = display.IsVirtual ? new[] { display.RefreshRate } : [],
                        displays = new[] { new
                        {
                            display.CaptureIndex, display.DeviceId, display.Name, display.Bounds, display.IsPrimary,
                            display.IsVirtual, display.Width, display.Height, display.RefreshRate, isSelected = true,
                            modes = new[] { new { display.Width, display.Height, fps = display.RefreshRate } },
                            scaleOptions = Array.Empty<int>()
                        } }
                    }, token);
                    break;
                }
                case "POST /v1/displays/select":
                {
                    using var requestBody = await ReadBodyAsync(stream, headers, token);
                    var body = requestBody.RootElement;
                    var display = await X11Display.ReadAsync(token);
                    var id = ReadString(body, "deviceId");
                    var requestId = ReadString(body, "requestId");
                    if (requestId is not null && !Guid.TryParseExact(requestId, "D", out _)) throw new InvalidDataException();
                    if (display is null) { await ReplyAsync(stream, 503, new { error = "display_unavailable" }, token); break; }
                    if ((id is not null && id != display.DeviceId) || ReadFlag(body, "gatherWindows") ||
                        (ReadFlag(body, "exclusive") && !display.IsVirtual) ||
                        (ReadNumber(body, "width") is int width && width != display.Width) ||
                        (ReadNumber(body, "height") is int height && height != display.Height) ||
                        (ReadNumber(body, "fps") is > 0 and var fps && fps != display.RefreshRate) ||
                        ReadNumber(body, "scalePercent") is > 0)
                    {
                        await ReplyAsync(stream, 409, new { error = "display_mode_unavailable" }, token);
                        break;
                    }
                    // The fixed capture target never changes desktop topology and leases no resources.
                    await ReplyAsync(stream, 200, new { status = "ready", requestId }, token);
                    break;
                }
                case "POST /v1/displays/lease":
                {
                    using var requestBody = await ReadBodyAsync(stream, headers, token);
                    if (!Guid.TryParseExact(ReadString(requestBody.RootElement, "requestId"), "D", out _) ||
                        ReadString(requestBody.RootElement, "action") is not ("keepalive" or "release" or "renew" or "abort" or "switch"))
                        throw new InvalidDataException();
                    await ReplyAsync(stream, 200, new { status = "accepted" }, token);
                    break;
                }
                case "POST /v1/transfer/pair":
                {
                    using var requestBody = await ReadBodyAsync(stream, headers, token);
                    var body = requestBody.RootElement;
                    if (ReadString(body, "transport") != "tls-v2")
                    {
                        await ReplyAsync(stream, 409, new { error = "transfer_update_required" }, token);
                        break;
                    }
                    var pair = state.PairTransfer(ReadString(body, "deviceId") ?? "", ReadString(body, "deviceName") ?? "", owner, address);
                    await ReplyAsync(stream, 200, pair, token);
                    break;
                }
                case "GET /v1/microphone":
                case "POST /v1/displays/session":
                case "POST /v1/displays/input-status":
                    if (bodyLength > 0) await stream.ReadExactlyAsync(new byte[bodyLength], token);
                    await ReplyAsync(stream, 501, new { error = "feature_unavailable" }, token);
                    break;
                default:
                    await ReplyAsync(stream, 404, new { error = "not_found" }, token);
                    break;
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or FormatException)
        { await ReplyAsync(stream, 400, new { error = "invalid_request" }, token); }
    }

    private static string? ReadString(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) ? value.GetString() : null;
    private static bool ReadFlag(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.GetBoolean();
    private static int? ReadNumber(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;

    private static async Task<string> ReadLineAsync(Stream stream, int limit, CancellationToken token)
    {
        var character = new byte[1];
        var line = new StringBuilder();
        while (line.Length < limit && await stream.ReadAsync(character, token) == 1)
        {
            if (character[0] == 10) return line.ToString().TrimEnd('\r');
            line.Append((char)character[0]);
        }
        throw new InvalidDataException();
    }

    private static async Task<JsonDocument> ReadBodyAsync(Stream stream, Dictionary<string, string> headers, CancellationToken token)
    {
        if (!headers.TryGetValue("Content-Length", out var text) || !int.TryParse(text, out var length) || length is < 2 or > 8192)
            throw new InvalidDataException();
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonDocument.Parse(bytes);
    }

    private static async Task ReplyAsync(Stream stream, int status, object response, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, HostState.Json);
        var reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict",
            501 => "Not Implemented", _ => "Service Unavailable"
        };
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"), token);
        await stream.WriteAsync(bytes, token);
    }

    private static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
              (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            : address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }
}
