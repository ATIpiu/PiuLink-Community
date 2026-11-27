using System.Text.RegularExpressions;

namespace PiuLink.Client.Core.Models;

public sealed record StreamProfile(
    string Resolution,
    int FramesPerSecond,
    int BitrateKbps,
    string DisplayMode,
    string VideoCodec,
    bool EnableHdr,
    bool DesktopMouseMode,
    bool GatherWindows = false,
    bool AutoBitrate = true,
    bool ContentAdaptive = false,
    bool ShowPerformanceOverlay = true,
    bool EnableVsync = true,
    bool FramePacing = false,
    string AudioConfiguration = "stereo",
    bool MultiController = true,
    bool PlayAudioOnHost = false,
    bool MuteOnFocusLoss = false,
    bool BackgroundGamepad = false,
    bool SwapGamepadFaceButtons = false,
    bool EnableYuv444 = false,
    string CaptureSystemKeys = "fullscreen",
    bool ShowVirtualGamepad = false,
    string ColorRange = "auto",
    int GamepadDeadzonePercent = 7,
    int DisplayScalePercent = 0)
{
    private static readonly Regex ResolutionPattern = new(
        "^[1-9][0-9]{2,4}x[1-9][0-9]{2,4}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static StreamProfile Default { get; } =
        new("1280x720", 60, 20000, "fullscreen", "auto", false, true);

    public void Validate()
    {
        if (!ResolutionPattern.IsMatch(Resolution))
        {
            throw new ArgumentException("Resolution must use WIDTHxHEIGHT format.", nameof(Resolution));
        }

        if (FramesPerSecond is < 10 or > 480)
        {
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond), "FPS must be between 10 and 480.");
        }

        if (BitrateKbps is < 500 or > 500000)
        {
            throw new ArgumentOutOfRangeException(nameof(BitrateKbps), "Bitrate must be between 500 and 500000 Kbps.");
        }

        if (DisplayMode is not ("fullscreen" or "windowed" or "borderless"))
        {
            throw new ArgumentException("Unsupported display mode.", nameof(DisplayMode));
        }

        if (VideoCodec is not ("auto" or "H.264" or "HEVC" or "AV1"))
        {
            throw new ArgumentException("Unsupported video codec.", nameof(VideoCodec));
        }

        if (AudioConfiguration is not ("stereo" or "5.1-surround" or "7.1-surround"))
        {
            throw new ArgumentException("Unsupported audio configuration.", nameof(AudioConfiguration));
        }

        if (CaptureSystemKeys is not ("never" or "fullscreen" or "always"))
        {
            throw new ArgumentException("Unsupported system key capture mode.", nameof(CaptureSystemKeys));
        }

        if (ColorRange is not ("auto" or "limited" or "full"))
        {
            throw new ArgumentException("Unsupported color range.", nameof(ColorRange));
        }

        if (GamepadDeadzonePercent is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(GamepadDeadzonePercent), "Gamepad deadzone must be between 0 and 20 percent.");
        }

        if (DisplayScalePercent != 0 && DisplayScalePercent is not
            (100 or 125 or 150 or 175 or 200 or 225 or 250 or 300 or 350 or 400 or 450 or 500))
        {
            throw new ArgumentOutOfRangeException(nameof(DisplayScalePercent),
                "Display scale must be a standard Windows percentage or zero to keep the current setting.");
        }
    }
}
