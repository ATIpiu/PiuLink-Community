using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiuLink.Client.Core.Streaming;

public static class PiuLinkEngineProtocol
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeCommand(PiuLinkEngineCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return JsonSerializer.Serialize(command, JsonOptions);
    }

    public static PiuLinkEngineTelemetry ParseTelemetry(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Engine telemetry cannot be empty.", nameof(json));
        var telemetry = JsonSerializer.Deserialize<PiuLinkEngineTelemetry>(json, JsonOptions)
            ?? throw new InvalidDataException("Engine telemetry was empty.");
        telemetry.Validate();
        return telemetry;
    }
}

public sealed record PiuLinkEngineCommand(
    int ProtocolVersion,
    string Command,
    int? Value = null,
    int? Buttons = null,
    int? LeftX = null,
    int? LeftY = null,
    int? RightX = null,
    int? RightY = null,
    int? LeftTrigger = null,
    int? RightTrigger = null)
{
    public static PiuLinkEngineCommand SetBitrate(int bitrateKbps) =>
        new(PiuLinkEngineProtocol.CurrentVersion, "setBitrateKbps", bitrateKbps);
    public static PiuLinkEngineCommand SetContentAdaptive(bool enabled) =>
        new(PiuLinkEngineProtocol.CurrentVersion, "setContentAdaptive", enabled ? 1 : 0);
    public static PiuLinkEngineCommand SetVirtualGamepad(
        int buttons, int leftX, int leftY, int rightX, int rightY, int leftTrigger, int rightTrigger) =>
        new(PiuLinkEngineProtocol.CurrentVersion, "setVirtualGamepad", null,
            buttons, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger);

    public void Validate()
    {
        if (ProtocolVersion != PiuLinkEngineProtocol.CurrentVersion)
            throw new InvalidDataException($"Unsupported PiuLink engine protocol version: {ProtocolVersion}.");
        if ((Command == "setBitrateKbps" && Value is >= 500 and <= 500000) ||
            (Command == "setContentAdaptive" && Value is 0 or 1)) return;
        if (Command == "setVirtualGamepad" && Buttons is >= 0 and <= 0x00FF_FFFF &&
            LeftX is >= short.MinValue and <= short.MaxValue && LeftY is >= short.MinValue and <= short.MaxValue &&
            RightX is >= short.MinValue and <= short.MaxValue && RightY is >= short.MinValue and <= short.MaxValue &&
            LeftTrigger is >= 0 and <= byte.MaxValue && RightTrigger is >= 0 and <= byte.MaxValue) return;
        throw new InvalidDataException($"Invalid engine command or value: {Command}.");
    }
}

public sealed record PiuLinkEngineTelemetry(
    int ProtocolVersion, long Sequence, string State, string Codec,
    int NegotiatedCeilingKbps, int AutoTargetKbps, int EngineTargetKbps, double ReceivedMbps,
    bool ContentAdaptiveRequested, bool ContentAdaptiveConfirmed, string ContentAdaptiveState,
    double FramesPerSecond, double NetworkDroppedPercent, double JitterDroppedPercent,
    int RoundTripTimeMs, int RoundTripVarianceMs, double DecodeTimeMs, double RenderTimeMs,
    string? Error = null)
{
    public void Validate()
    {
        if (ProtocolVersion != PiuLinkEngineProtocol.CurrentVersion)
            throw new InvalidDataException($"Unsupported PiuLink engine protocol version: {ProtocolVersion}.");
        if (Sequence < 0 || NegotiatedCeilingKbps < 0 || AutoTargetKbps < 0 || EngineTargetKbps < 0 || ReceivedMbps < 0)
            throw new InvalidDataException("Engine telemetry contains a negative counter or bitrate.");
        if (FramesPerSecond < 0 || NetworkDroppedPercent is < 0 or > 100 || JitterDroppedPercent is < 0 or > 100)
            throw new InvalidDataException("Engine telemetry contains an invalid frame or loss value.");
        if (RoundTripTimeMs < 0 || RoundTripVarianceMs < 0 || DecodeTimeMs < 0 || RenderTimeMs < 0)
            throw new InvalidDataException("Engine telemetry contains an invalid latency value.");
        if (string.IsNullOrWhiteSpace(State) || string.IsNullOrWhiteSpace(Codec) || string.IsNullOrWhiteSpace(ContentAdaptiveState))
            throw new InvalidDataException("Engine telemetry is missing a required state label.");
    }
}
