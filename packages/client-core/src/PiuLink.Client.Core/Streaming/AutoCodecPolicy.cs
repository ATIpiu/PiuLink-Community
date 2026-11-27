using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;

namespace PiuLink.Client.Core.Streaming;

public enum AutoCodecMode
{
    Balanced,
    Efficiency
}

public sealed record AutoCodecDecision(AutoCodecMode Mode, string Reason)
{
    public bool PreferAv1 => Mode == AutoCodecMode.Efficiency;
}

public static class AutoCodecPolicy
{
    private const long FullHdPixels = 1920L * 1080;

    public static AutoCodecDecision Decide(HostEndpoint endpoint, StreamProfile profile)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        if (!string.Equals(profile.VideoCodec, "auto", StringComparison.Ordinal))
            return new(AutoCodecMode.Balanced, $"manual:{profile.VideoCodec}");

        var dimensions = profile.Resolution.Split('x', 2);
        var pixels = long.Parse(dimensions[0], System.Globalization.CultureInfo.InvariantCulture) *
                     long.Parse(dimensions[1], System.Globalization.CultureInfo.InvariantCulture);
        if (endpoint.IsTailscaleAddress)
            return new(AutoCodecMode.Efficiency, "auto:tailscale");
        if (pixels > FullHdPixels)
            return new(AutoCodecMode.Efficiency, "auto:high-resolution");
        return new(AutoCodecMode.Balanced, profile.FramesPerSecond >= 120
            ? "auto:local-high-fps" : "auto:local-low-latency");
    }
}
