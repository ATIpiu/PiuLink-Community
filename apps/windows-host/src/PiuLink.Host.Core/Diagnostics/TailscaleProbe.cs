using System.Text.Json;
using PiuLink.Host.Core.Models;

namespace PiuLink.Host.Core.Diagnostics;

public sealed record TailscaleProbeResult(ComponentHealth Health, string? IpAddress, string? DeviceName);

public sealed class TailscaleProbe
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _executableLocator;

    public TailscaleProbe(IProcessRunner processRunner, Func<string?>? executableLocator = null)
    {
        _processRunner = processRunner;
        _executableLocator = executableLocator ?? ExecutableLocator.FindTailscale;
    }

    public async Task<TailscaleProbeResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var executable = _executableLocator();
        if (executable is null)
        {
            return new TailscaleProbeResult(
                new ComponentHealth("Tailscale", ComponentState.Missing, "Not installed"),
                null,
                null);
        }

        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                executable,
                ["status", "--json", "--peers=false"],
                TimeSpan.FromSeconds(8),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TailscaleProbeResult(
                new ComponentHealth("Tailscale", ComponentState.NeedsAttention, "Installed but unavailable", exception.Message),
                null,
                null);
        }

        if (!result.Succeeded)
        {
            var detail = result.TimedOut ? "Status check timed out." : FirstUsefulLine(result.StandardError);
            return new TailscaleProbeResult(
                new ComponentHealth("Tailscale", ComponentState.NeedsAttention, "Installed; sign-in or service required", detail),
                null,
                null);
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var state = ReadString(root, "BackendState") ?? "Unknown";
            var ip = ReadFirstArrayString(root, "TailscaleIPs");
            var deviceName = root.TryGetProperty("Self", out var self)
                ? ReadString(self, "HostName") ?? ReadString(self, "DNSName")?.TrimEnd('.')
                : null;
            var ready = state.Equals("Running", StringComparison.OrdinalIgnoreCase) && ip is not null;

            return new TailscaleProbeResult(
                new ComponentHealth(
                    "Tailscale",
                    ready ? ComponentState.Ready : ComponentState.NeedsAttention,
                    ready ? $"Connected · {ip}" : $"Backend state: {state}",
                    ready ? null : "Use PiuLink's private-network guide to complete sign-in."),
                ip,
                deviceName);
        }
        catch (JsonException exception)
        {
            return new TailscaleProbeResult(
                new ComponentHealth("Tailscale", ComponentState.NeedsAttention, "Status output was not valid JSON", exception.Message),
                null,
                null);
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadFirstArrayString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                return item.GetString();
            }
        }

        return null;
    }

    private static string FirstUsefulLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
        ?? "No diagnostic detail was returned.";
}
