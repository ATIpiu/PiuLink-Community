using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;

namespace PiuLink.Client.Core.Streaming;

public static class MoonlightCommandBuilder
{
    public static IReadOnlyList<string> Pair(HostEndpoint endpoint, string pin)
    {
        if (pin.Length != 4 || pin.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("Pairing PIN must contain exactly four digits.", nameof(pin));
        }

        return ["pair", "--pin", pin, endpoint.MoonlightTarget];
    }

    public static IReadOnlyList<string> ListApps(HostEndpoint endpoint) => ["list", endpoint.MoonlightTarget];
    public static IReadOnlyList<string> Quit(HostEndpoint endpoint) => ["quit", endpoint.MoonlightTarget];

    public static IReadOnlyList<string> Stream(
        HostEndpoint endpoint,
        string appName,
        StreamProfile profile,
        bool integratedEngine = true)
    {
        if (string.IsNullOrWhiteSpace(appName) || appName.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Select a valid app.", nameof(appName));
        }

        profile.Validate();

        var arguments = new List<string>
        {
            "stream",
            "--resolution", profile.Resolution,
            "--fps", profile.FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--bitrate", profile.BitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--display-mode", profile.DisplayMode,
            "--video-codec", profile.VideoCodec,
            "--audio-config", profile.AudioConfiguration,
            "--keep-awake",
            "--game-optimization",
            profile.EnableVsync ? "--vsync" : "--no-vsync",
            profile.FramePacing ? "--frame-pacing" : "--no-frame-pacing",
            profile.MultiController ? "--multi-controller" : "--no-multi-controller",
            profile.PlayAudioOnHost ? "--audio-on-host" : "--no-audio-on-host",
            profile.MuteOnFocusLoss ? "--mute-on-focus-loss" : "--no-mute-on-focus-loss",
            profile.BackgroundGamepad ? "--background-gamepad" : "--no-background-gamepad",
            profile.SwapGamepadFaceButtons ? "--swap-gamepad-buttons" : "--no-swap-gamepad-buttons",
            profile.EnableYuv444 ? "--yuv444" : "--no-yuv444",
            "--capture-system-keys", profile.CaptureSystemKeys,
            profile.ShowPerformanceOverlay ? "--performance-overlay" : "--no-performance-overlay",
            profile.EnableHdr ? "--hdr" : "--no-hdr",
            profile.DesktopMouseMode ? "--absolute-mouse" : "--no-absolute-mouse",
        };
        if (integratedEngine) {
            arguments.Add("--gamepad-deadzone");
            arguments.Add(profile.GamepadDeadzonePercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        arguments.Add(endpoint.MoonlightTarget);
        arguments.Add(appName.Trim());

        return arguments;
    }
}
