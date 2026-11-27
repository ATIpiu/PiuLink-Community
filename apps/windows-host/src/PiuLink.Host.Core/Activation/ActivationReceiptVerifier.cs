using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiuLink.Host.Core.Activation;

public enum ActivationReceiptState
{
    Valid,
    Missing,
    Invalid,
    WrongDevice
}

public sealed record ActivationReceiptDecision(
    ActivationReceiptState State,
    bool CanRun,
    string? ActivationId = null,
    int? ActivationCount = null,
    int? MaxActivations = null);

public sealed record ActivationReceiptPayload(
    [property: JsonPropertyName("ver")] int Version,
    [property: JsonPropertyName("jti")] string ActivationId,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("device")] string DeviceHash,
    [property: JsonPropertyName("iat")] long IssuedAt,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("activation_count")] int? ActivationCount = null,
    [property: JsonPropertyName("max_activations")] int? MaxActivations = null);

public static class ActivationReceiptVerifier
{
    public static ActivationReceiptDecision Verify(
        string? receipt,
        string expectedDeviceHash,
        string publicKeyPem,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(receipt))
        {
            return new ActivationReceiptDecision(ActivationReceiptState.Missing, false);
        }

        try
        {
            var segments = receipt.Split('.');
            if (segments.Length != 3)
            {
                return Invalid();
            }

            using var header = JsonDocument.Parse(Decode(segments[0]));
            if (!header.RootElement.TryGetProperty("alg", out var algorithm) ||
                algorithm.GetString() != "ES256" ||
                !header.RootElement.TryGetProperty("typ", out var type) ||
                type.GetString() != "PIULINK-ACTIVATION")
            {
                return Invalid();
            }

            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var verified = key.VerifyData(
                Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
                Decode(segments[2]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!verified)
            {
                return Invalid();
            }

            var payload = JsonSerializer.Deserialize<ActivationReceiptPayload>(Decode(segments[1]));
            if (payload is null ||
                payload.Version != 1 ||
                payload.Product != "piulink-host" ||
                payload.License != "perpetual" ||
                string.IsNullOrWhiteSpace(payload.ActivationId) ||
                string.IsNullOrWhiteSpace(payload.DeviceHash) ||
                payload.ActivationCount.HasValue != payload.MaxActivations.HasValue ||
                (payload.ActivationCount.HasValue && (payload.MaxActivations != 2 ||
                    payload.ActivationCount is < 1 or > 2)) ||
                payload.IssuedAt > (now ?? DateTimeOffset.UtcNow).AddMinutes(5).ToUnixTimeSeconds())
            {
                return Invalid();
            }

            if (!FixedTimeTextEquals(payload.DeviceHash, expectedDeviceHash))
            {
                return new ActivationReceiptDecision(ActivationReceiptState.WrongDevice, false);
            }

            return new ActivationReceiptDecision(ActivationReceiptState.Valid, true, payload.ActivationId,
                payload.ActivationCount, payload.MaxActivations);
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or CryptographicException or ArgumentException)
        {
            return Invalid();
        }
    }

    private static ActivationReceiptDecision Invalid() =>
        new(ActivationReceiptState.Invalid, false);

    private static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid Base64Url value.")
        };
        return Convert.FromBase64String(normalized);
    }

    private static bool FixedTimeTextEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
