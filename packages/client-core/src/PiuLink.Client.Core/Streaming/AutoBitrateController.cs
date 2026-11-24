using PiuLink.Client.Core.Networking;
using System.Net;
using System.Net.Sockets;

namespace PiuLink.Client.Core.Streaming;

// Keeps 60-second received traffic for diagnostics. It never changes the
// target after the stream starts: received traffic is content demand, not
// available network bandwidth.
public sealed class AutoBitrateController
{
    private readonly Queue<(long TimeMs, int Kbps)> _throughput = new();

    public AutoBitrateController(int targetKbps)
    {
        if (targetKbps is < 500 or > 500000) throw new ArgumentOutOfRangeException(nameof(targetKbps));
        TargetKbps = targetKbps;
    }

    public static int RecommendedStartupKbps(HostEndpoint endpoint, string resolution, int fps)
    {
        var size = resolution.Split('x');
        var pixelsPerSecond = (long)int.Parse(size[0]) * int.Parse(size[1]) * fps;
        var baseKbps = pixelsPerSecond >= 3840L * 2160 * 60 ? 60000 :
            pixelsPerSecond >= 2560L * 1440 * 60 ? 45000 :
            pixelsPerSecond >= 1920L * 1080 * 60 ? 30000 : 20000;
        if (endpoint.IsTailscaleAddress) return Math.Min(baseKbps, 25000);
        if (IPAddress.TryParse(endpoint.Host, out var address))
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                    bytes[0] == 192 && bytes[1] == 168) return baseKbps;
            }
            else if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return baseKbps;
        }
        return Math.Min(baseKbps, 15000);
    }

    public int TargetKbps { get; }
    public int HistoricalThroughputKbps
    {
        get
        {
            if (_throughput.Count == 0) return 0;
            var ordered = _throughput.Select(sample => sample.Kbps).OrderBy(value => value).ToArray();
            return ordered[(ordered.Length - 1) / 4];
        }
    }

    public void Observe(PiuLinkEngineTelemetry sample, long nowMs)
    {
        while (_throughput.Count > 0 && nowMs - _throughput.Peek().TimeMs >= 60000)
            _throughput.Dequeue();
        var receivedKbps = (int)Math.Round(sample.ReceivedMbps * 1000);
        if (receivedKbps > 0) _throughput.Enqueue((nowMs, receivedKbps));
    }
}
