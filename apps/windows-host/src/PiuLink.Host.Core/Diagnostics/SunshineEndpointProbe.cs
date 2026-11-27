using System.Net;
using System.Net.Sockets;
using PiuLink.Host.Core.Models;

namespace PiuLink.Host.Core.Diagnostics;

public sealed class SunshineEndpointProbe
{
    private const int SunshineWebPort = 47990;

    public async Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, SunshineWebPort, linked.Token);
            return new ComponentHealth(
                "Sunshine Web",
                ComponentState.Ready,
                $"Local endpoint listening on {SunshineWebPort}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new ComponentHealth(
                "Sunshine Web",
                ComponentState.NeedsAttention,
                "Local endpoint timed out",
                $"Sunshine did not accept a connection on localhost:{SunshineWebPort}.");
        }
        catch (SocketException exception)
        {
            return new ComponentHealth(
                "Sunshine Web",
                ComponentState.NeedsAttention,
                "Local endpoint unavailable",
                exception.SocketErrorCode.ToString());
        }
    }
}

