using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PiuLink.Host.Core.Diagnostics;

namespace PiuLink.Host.Services;

internal sealed class FileTransferServer : IDisposable
{
    public const int Port = 48012;
    internal const long MaximumFileBytes = 2_147_483_648;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PIULINK1");
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Func<string, string, string, bool> _validate;
    private readonly Func<X509Certificate?, bool> _isPaired;
    private readonly Func<X509Certificate2> _loadIdentity;
    private readonly string _inbox;
    private readonly bool _useIpv6;
    private readonly LocalLog _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly object _lifecycle = new();
    private TcpListener? _listener;
    private bool _disposed;

    public FileTransferServer(Func<string, string, string, bool> validate, LocalLog log,
        Func<X509Certificate?, bool> isPaired, Func<X509Certificate2> loadIdentity, string inbox, bool useIpv6 = true)
    {
        _validate = validate;
        _log = log;
        _isPaired = isPaired;
        _loadIdentity = loadIdentity;
        _inbox = inbox;
        _useIpv6 = useIpv6 && Socket.OSSupportsIPv6;
    }

    public bool Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return true;
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(_useIpv6 ? IPAddress.IPv6Any : IPAddress.Any, Port);
                if (_useIpv6) listener.Server.DualMode = true;
                listener.Start();
                _listener = listener;
                _ = _log.InfoAsync("transfer.host.listener_started", "file-transfer", "file receiver listener started",
                    fields: new Dictionary<string, object?> { ["port"] = Port, ["dual_mode"] = _useIpv6 });
                _ = AcceptLoopAsync(listener, _shutdown.Token);
                return true;
            }
            catch (SocketException error) { listener?.Stop(); _ = _log.ErrorAsync("transfer.host.listener_bind_failed", "file-transfer", "file receiver listener could not bind", error, fields: new Dictionary<string, object?> { ["port"] = Port, ["socket_error"] = error.SocketErrorCode.ToString() }); return false; }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(shutdown).ConfigureAwait(false);
                lock (_lifecycle)
                {
                    // Bound unauthenticated connections as well as active uploads.
                    if (_disposed || _clients.Count >= 4) { client.Dispose(); continue; }
                    _clients.TryAdd(client, 0);
                }
                _ = HandleAsync(client, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { return; }
            catch (SocketException error)
            {
                if (shutdown.IsCancellationRequested) return;
                _ = _log.ErrorAsync("transfer.host.accept_failed", "file-transfer", "file receiver accept failed", error, fields: new Dictionary<string, object?> { ["socket_error"] = error.SocketErrorCode.ToString() });
                try { await Task.Delay(250, shutdown).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken shutdown)
    {
        string? temporary = null;
        bool committed = false;
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var started = Stopwatch.GetTimestamp();
        string stage = "accepted";
        long expectedBytes = 0;
        long receivedBytes = 0;
        _ = _log.InfoAsync("transfer.host.accepted", "file-transfer", "incoming file transfer accepted", operationId,
            fields: new Dictionary<string, object?> { ["remote_family"] = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.AddressFamily.ToString() : null });
        using (client)
        using (var total = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token))
        {
            total.CancelAfter(TimeSpan.FromHours(1));
            SslStream? stream = null;
            try
            {
                client.NoDelay = true;
                stage = "tls";
                idle.CancelAfter(TimeSpan.FromSeconds(10));
                stream = FileTransferTls.Create(client.GetStream(), null, _isPaired);
                using var identity = _loadIdentity();
                await FileTransferTls.AcceptAsync(stream, identity, idle.Token).ConfigureAwait(false);
                var certificateFingerprint = FileTransferTls.Fingerprint(stream.RemoteCertificate!);
                stage = "header";
                // ReceiveTimeout does not govern ReadAsync. Bound the complete unauthenticated header.
                idle.CancelAfter(TimeSpan.FromSeconds(10));
                var header = new byte[22];
                await stream.ReadExactlyAsync(header, idle.Token).ConfigureAwait(false);
                if (!header.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Invalid protocol.");
                var deviceLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
                var tokenLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10, 2));
                var nameLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(12, 2));
                var size = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(14, 8));
                expectedBytes = size;
                if (deviceLength is < 8 or > 128 || tokenLength is < 32 or > 128 ||
                    nameLength is < 1 or > 512 || size is < 0 or > MaximumFileBytes)
                    throw new InvalidDataException("Invalid transfer header.");
                var fields = new byte[deviceLength + tokenLength + nameLength];
                await stream.ReadExactlyAsync(fields, idle.Token).ConfigureAwait(false);
                var deviceId = Utf8.GetString(fields, 0, deviceLength);
                var token = Utf8.GetString(fields, deviceLength, tokenLength);
                var name = SafeName(Utf8.GetString(fields, deviceLength + tokenLength, nameLength));
                stage = "authorize";
                if (!_isPaired(stream.RemoteCertificate) || !_validate(deviceId, token, certificateFingerprint))
                    throw new UnauthorizedAccessException();
                _ = _log.InfoAsync("transfer.host.authorized", "file-transfer", "incoming file transfer authorized", operationId,
                    fields: new Dictionary<string, object?> { ["size_bytes"] = size, ["name_length"] = nameLength });
                idle.Token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_inbox);
                temporary = Path.Combine(_inbox, ".piulink-" + Guid.NewGuid().ToString("N") + ".tmp");
                stage = "payload";
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    var remaining = size;
                    var buffer = new byte[65536];
                    while (remaining > 0)
                    {
                        idle.CancelAfter(TimeSpan.FromMinutes(2));
                        var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token).ConfigureAwait(false);
                        if (count == 0) throw new EndOfStreamException("Transfer interrupted.");
                        await output.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
                        remaining -= count;
                        receivedBytes += count;
                    }
                    idle.CancelAfter(TimeSpan.FromMinutes(2));
                    await output.FlushAsync(idle.Token).ConfigureAwait(false);
                }
                idle.Token.ThrowIfCancellationRequested();
                stage = "commit";
                var committedPath = CommitUnique(temporary, _inbox, name);
                temporary = null;
                committed = true;
                _ = _log.InfoAsync("transfer.host.committed", "file-transfer", "file committed to downloads", operationId,
                    fields: new Dictionary<string, object?> { ["size_bytes"] = receivedBytes, ["saved_name"] = Path.GetFileName(committedPath) });
                stage = "ack";
                idle.CancelAfter(TimeSpan.FromSeconds(5));
                await stream.WriteAsync(new byte[] { 1 }, idle.Token).ConfigureAwait(false);
                _ = _log.InfoAsync("transfer.host.completed", "file-transfer", "file transfer completed and acknowledged", operationId,
                    fields: new Dictionary<string, object?> { ["size_bytes"] = receivedBytes, ["elapsed_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
            catch (Exception error)
            {
                _ = _log.ErrorAsync("transfer.host.failed", "file-transfer", "file receive failed", error, operationId,
                    fields: new Dictionary<string, object?> { ["stage"] = stage, ["expected_bytes"] = expectedBytes, ["received_bytes"] = receivedBytes, ["committed"] = committed, ["elapsed_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                if (!committed && stream is { IsAuthenticated: true } && !shutdown.IsCancellationRequested)
                {
                    try
                    {
                        using var ackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await stream.WriteAsync(new byte[] { 0 }, ackTimeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { /* Best-effort failure ACK; the peer may have disconnected. */ }
                }
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { _ = _log.ErrorAsync("transfer.host.temp_cleanup_failed", "file-transfer", "temporary file cleanup failed", error, operationId); }
                }
                stream?.Dispose();
                _clients.TryRemove(client, out _);
            }
        }
    }

    internal static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(value.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) name = "shared-file";
        // Leave space for the collision suffix on filesystems with a 255-byte name limit.
        var byteBudget = OperatingSystem.IsLinux() ? 240 : int.MaxValue;
        var safeLength = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (safeLength + rune.Utf16SequenceLength > 180 || rune.Utf8SequenceLength > byteBudget) break;
            safeLength += rune.Utf16SequenceLength;
            byteBudget -= rune.Utf8SequenceLength;
        }
        name = name[..safeLength];
        name = name.TrimEnd('.', ' ');
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
            name = "_" + name;
        return string.IsNullOrEmpty(name) ? "shared-file" : name;
    }

    internal static string CommitUnique(string temporary, string folder, string name)
    {
        byte[]? incomingHash = null;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 1; index < 10000; index++)
        {
            var target = Path.Combine(folder, index == 1 ? name : $"{stem} ({index}){extension}");
            try { File.Move(temporary, target, overwrite: false); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target))
            {
                var item = new FileInfo(target);
                if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    item.Length != new FileInfo(temporary).Length) continue;
                static byte[] Hash(string path) {
                    using var file = File.OpenRead(path);
                    return System.Security.Cryptography.SHA256.HashData(file);
                }
                incomingHash ??= Hash(temporary);
                try {
                    if (!incomingHash.AsSpan().SequenceEqual(Hash(target))) continue;
                } catch (FileNotFoundException) { index--; continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                File.Delete(temporary);
                return target;
            }
        }
        throw new IOException("Too many duplicate files.");
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _listener?.Stop();
            foreach (var client in _clients.Keys) client.Dispose();
            // Linked cancellation sources are still being unwound by workers.
            // Do not dispose the source while the accept loop can register with it.
        }
    }
}
