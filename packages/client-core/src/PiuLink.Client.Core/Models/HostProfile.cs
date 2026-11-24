namespace PiuLink.Client.Core.Models;

public sealed record HostProfile(
    Guid Id,
    string Name,
    string Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastConnectedAt = null,
    StreamProfile? StreamSettings = null,
    string? DisplayId = null,
    string[]? RecentApplications = null,
    string? MacAddress = null,
    string? InviteLink = null);

public sealed record HostProbeResult(
    bool IsReachable,
    TimeSpan? Latency,
    string Detail)
{
    public static HostProbeResult Reachable(TimeSpan latency) =>
        new(true, latency, $"Sunshine control port responded in {Math.Max(1, latency.TotalMilliseconds):0} ms.");

    public static HostProbeResult Unreachable(string detail) => new(false, null, detail);
}
