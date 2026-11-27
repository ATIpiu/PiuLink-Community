using System.Diagnostics;
using System.Net.Sockets;
using PiuLink.Client.Core.Models;

namespace PiuLink.Client.Core.Networking;

public sealed class HostProbeService
{
    public async Task<HostProbeResult> ProbeAsync(
        HostEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellation.CancelAfter(timeout);
        using var tcpClient = new TcpClient();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await tcpClient.ConnectAsync(endpoint.Host, endpoint.Port, linkedCancellation.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return HostProbeResult.Reachable(stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HostProbeResult.Unreachable($"Timed out connecting to {endpoint.Host}:{endpoint.Port}.");
        }
        catch (SocketException exception)
        {
            return HostProbeResult.Unreachable($"Could not reach {endpoint.Host}:{endpoint.Port}: {exception.SocketErrorCode}.");
        }
    }
}
