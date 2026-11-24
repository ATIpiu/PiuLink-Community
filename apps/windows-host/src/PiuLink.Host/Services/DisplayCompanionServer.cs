using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiuLink.Host.Core.Diagnostics;
using PiuLink.Host.Core.Sunshine;

namespace PiuLink.Host.Services;

/// <summary>
/// Publishes the Sunshine display inventory and applies an idle client-selected route.
/// The streaming protocol itself doesn't include monitor names or stable device IDs.
/// </summary>
public sealed class DisplayCompanionServer : IDisposable
{
    public const int Port = 48011;
    private static readonly (int Width, int Height)[] VirtualResolutions =
    [
        (1366, 768),
        (1920, 1080),
        (2560, 1440),
        (3840, 2160)
    ];
    private static readonly int[] VirtualRefreshRates = [60, 90, 120, 144, 165, 244];

    private readonly Func<CancellationToken, Task<SunshineDisplayState?>> _getState;
    private readonly Func<string?, bool, int?, int?, CancellationToken, Task<bool>> _selectDisplay;
    private readonly Func<string?, bool, int?, int?, bool, CancellationToken, Task<bool>>? _selectWithWindows;
    private readonly Func<string?, bool, int?, int?, bool, int?, CancellationToken, Task<bool>>? _selectWithScale;
    private readonly Func<SshAuthorizationRequest, CancellationToken, Task<bool>>? _authorizeSsh;
    private readonly Func<FileTransferPairRequest, CancellationToken, Task<FileTransferPairResponse?>>? _pairFileTransfer;
    private readonly CancellationTokenSource _shutdown = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private readonly SemaphoreSlim _connections = new(24, 24);
    private readonly SemaphoreSlim _microphone = new(1, 1);
    internal Func<string, string, CancellationToken, Task<bool>> OwnsActiveSession { get; set; } =
        (_, _, _) => Task.FromResult(false);
    internal Func<bool>? TextInputAtPointer { get; set; }
    internal Func<string, string, bool>? MicrophoneInUse { get; set; }
    internal Action<string, string>? EnsureMicrophone { get; set; }
    internal Func<IMicrophoneOutput> OpenMicrophone { get; set; } = RemoteMicrophoneOutput.Open;
    internal sealed record SessionDisplayCommand(string SessionId, string Action, string? Key, int? ExpectedCount);
    internal sealed record SessionDisplayState(DisplayTopologyService.SessionOutput[] Outputs, int? VirtualCount, string CreateStatus);
    internal Func<string, SessionDisplayCommand, CancellationToken, Task<SessionDisplayState>>? SessionDisplays { get; set; }
    public Func<string, string, string, CancellationToken, Task<bool>>? UpdateLease { get; set; }
    internal Func<X509Certificate2> LoadTlsCertificate { get; set; } = PairedDisplaySecurity.LoadServerCertificate;
    internal Func<X509Certificate?, bool> AuthenticatePeer { get; set; } = PairedDisplaySecurity.IsPaired;
    internal Func<SunshineDisplay, IReadOnlyList<DisplayTopologyService.DisplayMode>> ReadModes { get; set; } = display =>
        DisplayTopologyService.GetDisplayModes(DisplayTopologyService.ResolveDisplaySource(display.DisplayName, display.FriendlyName));

    public DisplayCompanionServer(
        Func<CancellationToken, Task<SunshineDisplayState?>> getState,
        Func<string?, bool, int?, int?, CancellationToken, Task<bool>> selectDisplay,
        Func<SshAuthorizationRequest, CancellationToken, Task<bool>>? authorizeSsh = null,
        Func<FileTransferPairRequest, CancellationToken, Task<FileTransferPairResponse?>>? pairFileTransfer = null)
    {
        _getState = getState;
        _selectDisplay = selectDisplay;
        _authorizeSsh = authorizeSsh;
        _pairFileTransfer = pairFileTransfer;
    }

    public DisplayCompanionServer(
        Func<CancellationToken, Task<SunshineDisplayState?>> getState,
        Func<string?, bool, int?, int?, bool, CancellationToken, Task<bool>> selectDisplay,
        Func<SshAuthorizationRequest, CancellationToken, Task<bool>>? authorizeSsh = null,
        Func<FileTransferPairRequest, CancellationToken, Task<FileTransferPairResponse?>>? pairFileTransfer = null)
        : this(getState, (id, exclusive, width, height, token) =>
            selectDisplay(id, exclusive, width, height, false, token), authorizeSsh, pairFileTransfer)
    {
        _selectWithWindows = selectDisplay;
    }

    public DisplayCompanionServer(
        Func<CancellationToken, Task<SunshineDisplayState?>> getState,
        Func<string?, bool, int?, int?, bool, int?, CancellationToken, Task<bool>> selectDisplay,
        Func<SshAuthorizationRequest, CancellationToken, Task<bool>>? authorizeSsh = null,
        Func<FileTransferPairRequest, CancellationToken, Task<FileTransferPairResponse?>>? pairFileTransfer = null)
        : this(getState, (id, exclusive, width, height, gather, token) =>
            selectDisplay(id, exclusive, width, height, gather, null, token),
            authorizeSsh, pairFileTransfer)
    {
        _selectWithScale = selectDisplay;
    }

    public bool Start()
    {
        if (_listener is not null) return true;
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, Port);
            _listener.Server.DualMode = true;
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_shutdown.Token);
            return true;
        }
        catch (SocketException)
        {
            _listener = null;
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                if (!await _connections.WaitAsync(0, cancellationToken)) { client.Dispose(); continue; }
                _ = HandleBoundedAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException error)
            {
                if (cancellationToken.IsCancellationRequested) return;
                // Windows can report a reset by a pending peer from AcceptTcpClientAsync.
                // The listening socket remains usable for the next connection.
                if (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                    continue;
                try { await Task.Delay(250, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleBoundedAsync(TcpClient client, CancellationToken shutdown)
    {
        try { await HandleAsync(client, shutdown); }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException) { }
        finally { client.Dispose(); _connections.Release(); }
    }

    internal async Task HandleAsync(TcpClient client, CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromSeconds(130));
        var cancellationToken = deadline.Token;
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            await using var network = client.GetStream();
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var prefix = new byte[1];
            if (await client.Client.ReceiveAsync(prefix, SocketFlags.Peek, readTimeout.Token) == 0) return;
            using var certificate = prefix[0] == 22 ? LoadTlsCertificate() : null;
            using var tls = certificate is null ? null : new SslStream(network, true,
                (_, peer, _, _) => AuthenticatePeer(peer));
            if (tls is not null)
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = certificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, readTimeout.Token);
            Stream stream = tls is null ? network : tls;
            var requestLine = await ReadHttpLineAsync(stream, readTimeout.Token);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var lineCount = 0; lineCount < 32; lineCount++)
            {
                var line = await ReadHttpLineAsync(stream, readTimeout.Token);
                if (string.IsNullOrEmpty(line)) break;
                var separator = line.IndexOf(':');
                if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            if (requestLine?.Contains(" /v1/displays", StringComparison.Ordinal) == true &&
                (tls?.RemoteCertificate is null || !AuthenticatePeer(tls.RemoteCertificate)))
            {
                // Drain a small bounded body so TCP can deliver the rejection without a reset.
                if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length is > 0 and <= 8192)
                    await stream.ReadExactlyAsync(new byte[length], readTimeout.Token);
                await WriteResponseAsync(stream, 403, new { error = "paired_tls_required" }, cancellationToken);
                return;
            }
            DisplayRequestContext.Owner.Value = tls?.RemoteCertificate is { } peerCertificate
                ? Convert.ToHexString(SHA256.HashData(peerCertificate.GetRawCertData())) : null;

            if (requestLine == "GET /v1/microphone HTTP/1.1")
            {
                if (tls?.RemoteCertificate is null || !AuthenticatePeer(tls.RemoteCertificate))
                    await WriteResponseAsync(stream, 403, new { error = "paired_tls_required" }, readTimeout.Token);
                else if (!headers.TryGetValue("Upgrade", out var protocol) || protocol != "piulink-microphone-v1" ||
                    !headers.TryGetValue("Connection", out var connection) || !connection.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                    !headers.TryGetValue("X-PiuLink-Session", out var session) || !Guid.TryParseExact(session, "D", out _))
                    await WriteResponseAsync(stream, 400, new { error = "invalid_microphone_request" }, readTimeout.Token);
                else
                {
                    client.NoDelay = true;
                    var owner = DisplayRequestContext.Owner.Value!;
                    await Task.Run(() => HandleMicrophoneAsync(tls, owner, session, shutdown), shutdown);
                }
                return;
            }

            if (requestLine is "GET /v1/ssh HTTP/1.1" or "GET /v1/ssh HTTP/1.0")
            {
                var defaults = SshDefaults.Read();
                await WriteResponseAsync(stream, 200, new {
                    username = defaults.Username, port = defaults.Port, platform = defaults.Platform
                }, cancellationToken);
                return;
            }

            if (requestLine is "POST /v1/ssh/authorize HTTP/1.1" or "POST /v1/ssh/authorize HTTP/1.0")
            {
                if (_authorizeSsh is null || client.Client.RemoteEndPoint is not IPEndPoint remote || !IsPrivatePeer(remote.Address))
                {
                    await WriteResponseAsync(stream, 403, new { error = "authorization_unavailable" }, cancellationToken);
                    return;
                }
                if (!TryReadContentLength(headers, 256, 4096, out var length))
                {
                    await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, cancellationToken);
                    return;
                }
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, readTimeout.Token);
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    var publicKey = root.GetProperty("publicKey").GetString() ?? "";
                    var deviceName = root.GetProperty("deviceName").GetString()?.Trim() ?? "Android device";
                    if (deviceName.Length is < 1 or > 80 || deviceName.Any(char.IsControl)) throw new JsonException();
                    var approved = await _authorizeSsh(
                        new SshAuthorizationRequest(publicKey, deviceName, remote.Address.ToString()), cancellationToken);
                    var defaults = SshDefaults.Read();
                    await WriteResponseAsync(stream, approved ? 200 : 403,
                        approved ? new { status = "authorized", username = defaults.Username, port = defaults.Port, platform = defaults.Platform }
                                 : new { error = "authorization_denied" }, cancellationToken);
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or ArgumentException or FormatException)
                {
                    await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await WriteResponseAsync(stream, 503, new { error = "authorization_failed" }, cancellationToken);
                }
                return;
            }

            if (requestLine is "POST /v1/transfer/pair HTTP/1.1" or "POST /v1/transfer/pair HTTP/1.0")
            {
                var certificateFingerprint = DisplayRequestContext.Owner.Value;
                if (_pairFileTransfer is null || client.Client.RemoteEndPoint is not IPEndPoint transferRemote ||
                    certificateFingerprint is null)
                { await WriteResponseAsync(stream, 403, new { error = "pairing_unavailable" }, cancellationToken); return; }
                if (!TryReadContentLength(headers, 20, 4096, out var transferLength))
                { await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, cancellationToken); return; }
                var transferBody = new byte[transferLength];
                await stream.ReadExactlyAsync(transferBody, readTimeout.Token);
                try
                {
                    using var document = JsonDocument.Parse(transferBody); var root = document.RootElement;
                    if (!root.TryGetProperty("transport", out var transport) || transport.GetString() != "tls-v2") {
                        await WriteResponseAsync(stream, 409, new { error = "transfer_update_required" }, cancellationToken); return;
                    }
                    var request = new FileTransferPairRequest(root.GetProperty("deviceId").GetString() ?? "", root.GetProperty("deviceName").GetString() ?? "Android device", root.TryGetProperty("token", out var token) ? token.GetString() : null, transferRemote.Address.ToString(),
                        root.TryGetProperty("refreshOnly", out var refresh) && refresh.GetBoolean(), certificateFingerprint);
                    if (request.DeviceName.Length is < 1 or > 80 || request.DeviceName.Any(char.IsControl)) throw new JsonException();
                    var response = await _pairFileTransfer(request, cancellationToken);
                    if (response is null) await WriteResponseAsync(stream, 403, new { error = "pairing_denied" }, cancellationToken);
                    else await WriteResponseAsync(stream, 200, response, cancellationToken);
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
                { await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { await WriteResponseAsync(stream, 503, new { error = "pairing_failed" }, cancellationToken); }
                return;
            }

            if (requestLine is "POST /v1/displays/input-status HTTP/1.1" or "POST /v1/displays/input-status HTTP/1.0") {
                await HandleInputStatusAsync(stream, headers, readTimeout.Token);
                return;
            }
            if (requestLine is "POST /v1/displays/session HTTP/1.1" or "POST /v1/displays/session HTTP/1.0") {
                await HandleSessionDisplaysAsync(stream, headers, readTimeout.Token, cancellationToken);
                return;
            }

            if (requestLine is "POST /v1/displays/lease HTTP/1.1" or "POST /v1/displays/lease HTTP/1.0") {
                if (!TryReadContentLength(headers, 2, 1024, out var leaseLength) || UpdateLease is null) {
                    await WriteResponseAsync(stream, 400, new { error = "invalid_lease" }, cancellationToken); return;
                }
                var leaseBody = new byte[leaseLength];
                await stream.ReadExactlyAsync(leaseBody, readTimeout.Token);
                try {
                    using var leaseJson = JsonDocument.Parse(leaseBody);
                    var requestId = leaseJson.RootElement.GetProperty("requestId").GetString() ?? "";
                    var action = leaseJson.RootElement.GetProperty("action").GetString() ?? "";
                    var ok = await UpdateLease(DisplayRequestContext.Owner.Value!, requestId, action, cancellationToken);
                    await WriteResponseAsync(stream, ok ? 200 : 409, new { status = ok ? "accepted" : "conflict" }, cancellationToken);
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException) {
                    await WriteResponseAsync(stream, 400, new { error = "invalid_lease" }, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException) {
                    await WriteResponseAsync(stream, 503, new { error = "lease_update_failed" }, cancellationToken);
                }
                return;
            }

            if (string.Equals(requestLine, "GET /v1/displays HTTP/1.1", StringComparison.Ordinal) ||
                string.Equals(requestLine, "GET /v1/displays HTTP/1.0", StringComparison.Ordinal))
            {
                await WriteDisplaysAsync(stream, cancellationToken);
                return;
            }

            if (string.Equals(requestLine, "POST /v1/displays/select HTTP/1.1", StringComparison.Ordinal) ||
                string.Equals(requestLine, "POST /v1/displays/select HTTP/1.0", StringComparison.Ordinal))
            {
                if (!headers.TryGetValue("Content-Length", out var lengthText) ||
                    !int.TryParse(lengthText, out var length) || length is < 2 or > 4096)
                {
                    await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, cancellationToken);
                    return;
                }
                var buffer = new byte[length];
                await stream.ReadExactlyAsync(buffer, readTimeout.Token);
                try
                {
                    using var document = JsonDocument.Parse(buffer);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("exclusive", out var exclusiveField) ||
                        exclusiveField.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        (root.TryGetProperty("deviceId", out var deviceField) &&
                         deviceField.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) ||
                        (root.TryGetProperty("gatherWindows", out var gatherField) &&
                         gatherField.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
                        !IsValidOptionalPositiveInt(root, "width") || !IsValidOptionalPositiveInt(root, "height") ||
                        !IsValidOptionalPositiveInt(root, "scalePercent") || !IsValidOptionalPositiveInt(root, "fps"))
                    {
                        await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, cancellationToken);
                        return;
                    }
                    var deviceId = root.TryGetProperty("deviceId", out var deviceValue) &&
                                   deviceValue.ValueKind == JsonValueKind.String
                        ? deviceValue.GetString()
                        : null;
                    var exclusive = root.TryGetProperty("exclusive", out var exclusiveValue) &&
                                    exclusiveValue.ValueKind == JsonValueKind.True;
                    var width = ReadPositiveInt(root, "width");
                    var height = ReadPositiveInt(root, "height");
                    var gatherWindows = root.TryGetProperty("gatherWindows", out var gather) && gather.GetBoolean();
                    var scalePercent = ReadPositiveInt(root, "scalePercent");
                    DisplayRequestContext.FramesPerSecond.Value = ReadPositiveInt(root, "fps");
                    DisplayRequestContext.Mode.Value = width.HasValue ? $"{width}x{height}x{DisplayRequestContext.FramesPerSecond.Value ?? 60}" : "";
                    DisplayRequestContext.RequestId.Value = root.TryGetProperty("requestId", out var requestId)
                        && Guid.TryParse(requestId.GetString(), out var id) ? id.ToString() : Guid.NewGuid().ToString();
                    if (DisplayRequestContext.FramesPerSecond.Value is < 30 or > 480) throw new JsonException("Invalid refresh rate");
                    if (gatherWindows && (exclusive || string.IsNullOrWhiteSpace(deviceId) || _selectWithWindows is null))
                    {
                        await WriteResponseAsync(stream, 400, new { error = "invalid_window_target" }, cancellationToken);
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(deviceId) && !Guid.TryParse(deviceId, out _))
                    {
                        await WriteResponseAsync(stream, 400, new { error = "invalid_display" }, cancellationToken);
                        return;
                    }
                    if (exclusive && string.IsNullOrWhiteSpace(deviceId))
                    {
                        await WriteResponseAsync(stream, 400, new { error = "exclusive_requires_display" }, cancellationToken);
                        return;
                    }
                    if (scalePercent.HasValue &&
                        (string.IsNullOrWhiteSpace(deviceId) || _selectWithScale is null ||
                         !DisplayTopologyService.SupportedScalePercents.Contains(scalePercent.Value)))
                    {
                        await WriteResponseAsync(stream, 400, new { error = "invalid_display_scale" }, cancellationToken);
                        return;
                    }
                    if ((width.HasValue != height.HasValue) ||
                        (exclusive && width.HasValue && (width > 16384 || height > 16384)) ||
                        (!exclusive && width.HasValue))
                    {
                        await WriteResponseAsync(stream, 400, new { error = "invalid_resolution" }, cancellationToken);
                        return;
                    }
                    try
                    {
                        var applied = _selectWithScale is not null
                            ? await _selectWithScale(deviceId, exclusive, width, height, gatherWindows,
                                scalePercent, cancellationToken)
                            : _selectWithWindows is not null
                            ? await _selectWithWindows(deviceId, exclusive, width, height, gatherWindows, cancellationToken)
                            : await _selectDisplay(deviceId, exclusive, width, height, cancellationToken);
                        await WriteResponseAsync(stream, applied ? 200 : 409,
                            applied ? new { status = "ready", requestId = DisplayRequestContext.RequestId.Value,
                                expiresAt = DateTimeOffset.UtcNow.AddSeconds(45) } : new { error = "stream_active" }, cancellationToken);
                    }
                    catch (RetainedDisplayRouteException)
                    {
                        await WriteResponseAsync(stream, 409, new { error = "retained_application" }, cancellationToken);
                    }
                    catch (DisplayRecoveryFaultException)
                    {
                        await WriteResponseAsync(stream, 409, new { error = "display_recovery_failed" }, cancellationToken);
                    }
                    catch (DisplayRecoveryInProgressException)
                    {
                        await WriteResponseAsync(stream, 409, new { error = "display_recovering" }, cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await WriteResponseAsync(stream, 503,
                            new { error = "display_prepare_failed" }, cancellationToken);
                    }
                }
                catch (JsonException)
                {
                    await WriteResponseAsync(stream, 400, new { error = "invalid_json" }, cancellationToken);
                }
                return;
            }

            await WriteResponseAsync(stream, 404, new { error = "not_found" }, cancellationToken);
        }
    }

    private async Task HandleInputStatusAsync(Stream stream, Dictionary<string, string> headers, CancellationToken token)
    {
        if (!TryReadContentLength(headers, 2, 512, out var length)) {
            await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, token);
            return;
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        try {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sessionId", out var id) ||
                id.ValueKind != JsonValueKind.String || !Guid.TryParseExact(id.GetString(), "D", out _) ||
                !root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String)
                throw new JsonException();
            var owner = DisplayRequestContext.Owner.Value!;
            var session = id.GetString()!;
            if (action.GetString() is "viewportCaret" or "viewportPointer") {
                if (!await OwnsActiveSession(owner, session, token)) {
                    await WriteResponseAsync(stream, 403, new { error = "session_required" }, token);
                    return;
                }
                var entry = DisplayRecoveryJournal.Read();
                var bounds = entry?.Owner == owner && entry.RequestId == session
                    ? DisplayTopologyService.CaptureBounds(entry.CaptureKey) : null;
                var points = SessionInputStatus.Viewport(bounds, action.GetString() == "viewportCaret");
                await WriteResponseAsync(stream, 200, points, token);
                return;
            }
            Func<bool>? probe = action.GetString() switch {
                "textFocus" => TextInputAtPointer,
                "virtualMicrophoneUse" => MicrophoneInUse == null ? null : () => MicrophoneInUse(owner, session),
                _ => throw new JsonException()
            };
            if (probe == null) { await WriteResponseAsync(stream, 404, new { error = "unsupported" }, token); return; }
            if (!await OwnsActiveSession(owner, session, token)) {
                await WriteResponseAsync(stream, 403, new { error = "session_required" }, token);
                return;
            }
            var active = await Task.Run(probe, token).WaitAsync(token);
            await WriteResponseAsync(stream, 200, new { active }, token);
        }
        catch (JsonException) { await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, token); }
        catch (Exception error) when (error is not OperationCanceledException) {
            await WriteResponseAsync(stream, 503, new { error = "input_unavailable" }, token);
        }
    }

    private async Task HandleSessionDisplaysAsync(Stream stream, Dictionary<string, string> headers,
        CancellationToken readToken, CancellationToken token)
    {
        if (!TryReadContentLength(headers, 2, 1024, out var length)) {
            await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, token); return;
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, readToken);
        var sessionDisplays = SessionDisplays;
        if (sessionDisplays == null) { await WriteResponseAsync(stream, 404, new { error = "unsupported" }, token); return; }
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var cmd = root.Deserialize<SessionDisplayCommand>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (cmd == null || !Guid.TryParseExact(cmd.SessionId, "D", out _) ||
                cmd.Action switch {
                    "list" => cmd.Key != null || cmd.ExpectedCount != null,
                    "capture" => !Guid.TryParse(cmd.Key, out _) || cmd.ExpectedCount != null,
                    "enable" or "disable" => cmd.Key?.Length != 64 || !cmd.Key.All(Uri.IsHexDigit) || cmd.ExpectedCount != null,
                    "create" => cmd.Key != null || cmd.ExpectedCount is null or < 0 or >= 16,
                    _ => true
                }) throw new JsonException();
            var result = await sessionDisplays(DisplayRequestContext.Owner.Value!, cmd, token);
            await WriteResponseAsync(stream, 200, result, token);
        }
        catch (JsonException) { await WriteResponseAsync(stream, 400, new { error = "invalid_request" }, token); }
        catch (UnauthorizedAccessException) { await WriteResponseAsync(stream, 403, new { error = "session_required" }, token); }
        catch (InvalidOperationException error) {
            var reason = error.Message is "outputs_changed" or "output_protected" or "creation_unconfirmed" or "output_restore_pending"
                ? error.Message : "output_unavailable";
            await WriteResponseAsync(stream, 409, new { error = reason }, token);
        }
        catch (Exception error) when (error is not OperationCanceledException) {
            await WriteResponseAsync(stream, 503, new { error = "output_unavailable" }, token);
        }
    }

    private static async Task<string> ReadHttpLineAsync(Stream stream, CancellationToken token)
    {
        // Never buffer ahead into the UTF-8 body: Content-Length measures bytes.
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < 8192)
        {
            await stream.ReadExactlyAsync(single, token);
            if (single[0] == 10)
            {
                if (bytes.Count == 0 || bytes[^1] != 13) throw new InvalidDataException("Invalid HTTP line.");
                return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 1);
            }
            bytes.Add(single[0]);
        }
        throw new InvalidDataException("HTTP header too large.");
    }

    private async Task WriteDisplaysAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var state = await _getState(cancellationToken);
            if (state is null)
            {
                await WriteResponseAsync(stream, 503, new { error = "display_inventory_unavailable" }, cancellationToken);
                return;
            }

            var modesByTarget = state.Displays.Where(display => display.IsVirtual).ToDictionary(display => display.DeviceId,
                ReadModes);
            var displays = state.Displays.Select((display, index) =>
            {
                var scale = _selectWithScale is null
                    ? null
                    : DisplayTopologyService.TryGetDisplayScale(display.DisplayName);
                return new
                {
                    captureIndex = index,
                    deviceId = display.DeviceId,
                    name = !string.IsNullOrWhiteSpace(display.FriendlyName) ? display.FriendlyName :
                        !string.IsNullOrWhiteSpace(display.DisplayName) ? display.DisplayName : $"Display {index + 1}",
                    windowsName = display.DisplayName,
                    display.Bounds,
                    display.IsPrimary,
                    display.IsVirtual,
                    display.Width,
                    display.Height,
                    display.RefreshRate,
                    scalePercent = scale?.CurrentPercent,
                    scaleOptions = scale?.SupportedPercents ?? [],
                    modes = modesByTarget.GetValueOrDefault(display.DeviceId) ?? [],
                    isSelected = string.Equals(display.DeviceId, state.SelectedDeviceId,
                        StringComparison.OrdinalIgnoreCase)
                };
            }).ToArray();
            var commonModes = modesByTarget.Values.Aggregate((IEnumerable<DisplayTopologyService.DisplayMode>?)null,
                (common, modes) => common is null ? modes : common.Intersect(modes))?.ToArray() ?? [];
            var virtualResolutions = commonModes.Select(mode => new
            {
                width = mode.Width,
                height = mode.Height
            }).Distinct().ToArray();
            // Compatibility lists expose only combinations supported by every advertised target.
            var commonRates = commonModes.Select(mode => mode.Fps).Distinct().Where(rate =>
                virtualResolutions.All(size => commonModes.Any(mode => mode.Width == size.width && mode.Height == size.height && mode.Fps == rate))).ToArray();
            await WriteResponseAsync(stream, 200,
                new
                {
                    state.ExclusiveDisplay,
                    supportsSessionDisplays = SessionDisplays != null,
                    supportsWindowGathering = _selectWithWindows is not null,
                    supportsDisplayScaling = _selectWithScale is not null,
                    virtualResolutions,
                    virtualRefreshRates = commonRates,
                    observedAt = DateTimeOffset.UtcNow,
                    topologyEpoch = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(displays)))),
                    displays
                }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteResponseAsync(stream, 503, new { error = "display_inventory_unavailable" }, cancellationToken);
        }
    }

    private static int? ReadPositiveInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number) && number > 0
            ? number
            : null;
    }

    private static bool IsValidOptionalPositiveInt(JsonElement root, string propertyName) =>
        !root.TryGetProperty(propertyName, out var value) ||
        (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0);

    private static bool IsSupportedVirtualResolution(int width, int height) =>
        VirtualResolutions.Any(mode => mode.Width == width && mode.Height == height);

    private static bool TryReadContentLength(Dictionary<string, string> headers, int minimum, int maximum, out int length)
    {
        length = 0;
        return headers.TryGetValue("Content-Length", out var text) && int.TryParse(text, out length) &&
               length >= minimum && length <= maximum;
    }

    private static bool IsPrivatePeer(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] == 10 || bytes[0] == 127 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return address.IsIPv6LinkLocal || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    public static bool IsTrustedPairingPeer(IPAddress address, bool autoPairLan, bool autoPairTailnet)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        var tailnet = bytes.Length == 4
            ? bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            : bytes.Length == 16 && bytes[0] == 0xfd && bytes[1] == 0x7a &&
              bytes[2] == 0x11 && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0;
        return tailnet ? autoPairTailnet : autoPairLan && IsPrivatePeer(address);
    }

    private async Task HandleMicrophoneAsync(Stream stream, string owner, string session, CancellationToken shutdown)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        opening.CancelAfter(TimeSpan.FromSeconds(8));
        if (!await IsCurrentSession(opening.Token))
        {
            await WriteResponseAsync(stream, 403, new { error = "microphone_session_inactive" }, shutdown);
            return;
        }
        if (!await _microphone.WaitAsync(0, shutdown))
        {
            await WriteResponseAsync(stream, 409, new { error = "microphone_busy" }, shutdown);
            return;
        }
        try
        {
            IMicrophoneOutput output;
            try { output = OpenMicrophone(); }
            catch (Exception)
            {
                await WriteResponseAsync(stream, 503, new { error = "virtual_microphone_unavailable" }, opening.Token);
                return;
            }
            using (output)
            {
                try { EnsureMicrophone?.Invoke(owner, session); }
                catch (Exception) { /* Keep audio transport available even if default-role switching fails. */ }
                var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(output.RecordingDeviceName));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
                    "Connection: Upgrade\r\nUpgrade: piulink-microphone-v1\r\n" +
                    $"X-PiuLink-Input: {encodedName}\r\n\r\n"), opening.Token);
                var monitor = MonitorSessionAsync();
                try
                {
                    var frame = new byte[RemoteMicrophoneOutput.FrameBytes];
                    var ack = new byte[] { 1 };
                    int framesUntilAck = 1;
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(5));
                        if (await stream.ReadAsync(frame.AsMemory(0, 1), idle.Token) == 0) break;
                        await stream.ReadExactlyAsync(frame.AsMemory(1), idle.Token);
                        output.WriteFrame(frame);
                        if (--framesUntilAck == 0)
                        {
                            await stream.WriteAsync(ack, idle.Token);
                            framesUntilAck = 5;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Runtime.InteropServices.COMException) { }
                finally { lifetime.Cancel(); await monitor; }
            }
        }
        finally { _microphone.Release(); }

        async Task<bool> IsCurrentSession(CancellationToken token)
        {
            try { return await OwnsActiveSession(owner, session, token); }
            catch (Exception) { return false; }
        }

        async Task MonitorSessionAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    check.CancelAfter(TimeSpan.FromSeconds(2));
                    if (!await IsCurrentSession(check.Token)) { lifetime.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int statusCode,
        object body,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            _ => "Service Unavailable"
        };
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener?.Stop();
        _shutdown.Dispose();
    }
}

public sealed record SshAuthorizationRequest(string PublicKey, string DeviceName, string RemoteAddress);

internal sealed class RetainedDisplayRouteException() : InvalidOperationException("Changing this display route requires ending the retained application.");
internal sealed class DisplayRecoveryInProgressException() : InvalidOperationException("The previous display transaction is still owned or recovering.");

internal static class DisplayRequestContext
{
    public static readonly AsyncLocal<string?> Owner = new();
    public static readonly AsyncLocal<string?> RequestId = new();
    public static readonly AsyncLocal<int?> FramesPerSecond = new();
    public static readonly AsyncLocal<string?> Mode = new();
}

internal sealed class DisplayRecoveryFaultException() : InvalidOperationException("Display recovery requires attention.");
