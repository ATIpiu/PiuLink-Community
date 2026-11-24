using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PiuLink.Host.Services;

internal static partial class FileTransferSender
{
    internal static async Task SendToPeerAsync(Stream input, long size, string filename, string deviceId,
        string transferToken, string fingerprint, IEnumerable<string> addresses, X509Certificate2 identity,
        Func<X509Certificate?, bool> isPaired, CancellationToken token, IProgress<(long Sent, long Total)>? progress = null)
    {
        if (size is < 0 or > FileTransferServer.MaximumFileBytes) throw new IOException("文件不能超过 2 GiB。");
        var connection = await ConnectToKnownAddressAsync(addresses, fingerprint, identity, isPaired, token).ConfigureAwait(false);
        using var socket = connection.Client;
        await using var stream = connection.Stream;
        socket.NoDelay = true;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        await SendPayloadAsync(stream, input, size, filename, deviceId, transferToken, token, progress).ConfigureAwait(false);
    }

    internal static async Task SendPayloadAsync(SslStream stream, Stream input, long size, string filename,
        string deviceId, string transferToken, CancellationToken token, IProgress<(long Sent, long Total)>? progress = null)
    {
        var device = Encoding.UTF8.GetBytes(deviceId);
        var secret = Encoding.UTF8.GetBytes(transferToken);
        var name = Encoding.UTF8.GetBytes(filename);
        if (device.Length is < 8 or > 128 || secret.Length is < 32 or > 128)
            throw new IOException("文件传输授权无效，请重新配对。");
        if (name.Length is < 1 or > 512) throw new IOException("文件名过长。");
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        var header = new byte[22];
        Encoding.ASCII.GetBytes("PIULINK1").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)device.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)secret.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), (ushort)name.Length);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(14), size);
        try
        {
            idle.CancelAfter(TimeSpan.FromMinutes(2));
            await stream.WriteAsync(header, idle.Token).ConfigureAwait(false);
            await stream.WriteAsync(device, idle.Token).ConfigureAwait(false);
            await stream.WriteAsync(secret, idle.Token).ConfigureAwait(false);
            await stream.WriteAsync(name, idle.Token).ConfigureAwait(false);
            var buffer = new byte[65536];
            long remaining = size;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            progress?.Report((0, size));
            while (remaining > 0)
            {
                idle.CancelAfter(TimeSpan.FromMinutes(2));
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("发送过程中源文件被截断。");
                await stream.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
                remaining -= count;
                if (remaining == 0 || clock.ElapsedMilliseconds >= 200) {
                    progress?.Report((size - remaining, size));
                    clock.Restart();
                }
            }
            await stream.FlushAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("传输连续 2 分钟没有进展，已中止连接；请检查网络和手机存储。"); }
        var ack = new byte[1];
        idle.CancelAfter(TimeSpan.FromMinutes(3));
        try { await stream.ReadExactlyAsync(ack, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("未收到手机保存确认，结果未知。可以重试；新版接收端会校验已经保存的内容。"); }
        catch (EndOfStreamException)
        { throw new IOException("手机关闭连接且没有返回保存确认。请检查手机接收日志和存储空间。"); }
        if (ack[0] != 1) throw new IOException("手机拒绝或未能保存文件，请检查接收日志。");
    }

    private static async Task<(TcpClient Client, SslStream Stream)> ConnectToKnownAddressAsync(
        IEnumerable<string> knownAddresses, string fingerprint, X509Certificate2 identity,
        Func<X509Certificate?, bool> isPaired, CancellationToken token)
    {
        Exception? lastError = null;
        var addresses = new Queue<string>(knownAddresses
            .Where(address => System.Net.IPAddress.TryParse(address, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempts = new List<Task<(TcpClient Client, SslStream Stream)>>();
        Task nextAddress = Task.CompletedTask;
        try
        {
            while (addresses.Count > 0 || attempts.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (addresses.Count > 0 && (attempts.Count == 0 || nextAddress.IsCompleted))
                {
                    attempts.Add(ConnectAuthenticatedAsync(addresses.Dequeue(), fingerprint, identity, isPaired, race.Token));
                    if (addresses.Count > 0) nextAddress = Task.Delay(250, race.Token);
                }
                var result = Task.WhenAny(attempts);
                if (addresses.Count > 0 && await Task.WhenAny(result, nextAddress).ConfigureAwait(false) == nextAddress)
                    continue;
                var completed = await result.ConfigureAwait(false);
                attempts.Remove(completed);
                try { return await completed.ConfigureAwait(false); }
                catch (Exception error) when (error is SocketException or IOException or AuthenticationException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    lastError = error;
                }
            }
            throw new IOException("所有已保存的收件设备地址均无法连接。请检查接收服务与网络，并在收件设备上打开这台主机以刷新地址。", lastError);
        }
        finally
        {
            // Only the returned, certificate-verified connection is allowed to carry the file.
            race.Cancel();
            foreach (var unused in attempts)
                try
                {
                    var (client, stream) = await unused.ConfigureAwait(false);
                    using (client) stream.Dispose();
                }
                catch (Exception) { /* Failed attempts have already released their sockets. */ }
        }
    }

    private static async Task<(TcpClient Client, SslStream Stream)> ConnectAuthenticatedAsync(
        string address, string fingerprint, X509Certificate2 identity, Func<X509Certificate?, bool> isPaired, CancellationToken token)
    {
        var ip = System.Net.IPAddress.Parse(address);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var socket = new TcpClient(ip.AddressFamily);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        SslStream? stream = null;
        try
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            await socket.ConnectAsync(ip, FileTransferServer.Port, deadline.Token).ConfigureAwait(false);
            stream = FileTransferTls.Create(socket.GetStream(), fingerprint, isPaired);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await FileTransferTls.ConnectAsync(stream, identity, address, deadline.Token).ConfigureAwait(false);
            return (socket, stream);
        }
        catch
        {
            using (socket) stream?.Dispose();
            throw;
        }
    }
}
