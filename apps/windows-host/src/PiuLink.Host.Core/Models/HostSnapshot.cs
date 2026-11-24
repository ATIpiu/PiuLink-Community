using System.Runtime.InteropServices;

namespace PiuLink.Host.Core.Models;

public sealed record HostSnapshot(
    DateTimeOffset CheckedAt,
    string DeviceName,
    string WindowsVersion,
    Architecture Architecture,
    IReadOnlyList<GpuInfo> Gpus,
    ComponentHealth Tailscale,
    ComponentHealth Sunshine,
    string? TailscaleIp,
    ComponentHealth? SunshineEndpoint = null,
    ComponentHealth? Firewall = null,
    IReadOnlyList<string>? LanAddresses = null)
{
    private bool OverlayReady => Tailscale.IsReady && !string.IsNullOrWhiteSpace(TailscaleIp);
    public bool IsReady => Sunshine.IsReady && (SunshineEndpoint is null || SunshineEndpoint.IsReady) &&
        (LanAddresses is { Count: > 0 } || OverlayReady);

    public string ConnectionSummary => string.Join(Environment.NewLine, new[]
    {
        $"PiuLink Host: {DeviceName}",
        LanAddresses is { Count: > 0 } ? "LAN: " + string.Join(", ", LanAddresses) : null,
        OverlayReady ? $"Tailscale: {TailscaleIp}" : null,
        IsReady ? "Status: Ready" : "Status: Not ready — run diagnostics"
    }.Where(line => line is not null));
}
