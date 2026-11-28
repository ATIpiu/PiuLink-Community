using System.Text.Json;
using PiuLink.Host.Core.Models;

namespace PiuLink.Host.Core.Diagnostics;

public sealed class FirewallProbe(IProcessRunner processRunner)
{
    public async Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        const string command = "$filters = Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { $_.Program -like '*PiuLink.Engine.exe' -or $_.Program -like '*sunshine.exe' }; $rules = foreach ($filter in $filters) { Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $filter -ErrorAction SilentlyContinue }; $rules | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } | Select-Object -ExpandProperty DisplayName -Unique | ConvertTo-Json -Compress";
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                TimeSpan.FromSeconds(10),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ComponentHealth("Windows Firewall", ComponentState.Unknown, "Unable to inspect rules", exception.Message);
        }

        if (!result.Succeeded)
        {
            return new ComponentHealth(
                "Windows Firewall",
                ComponentState.Unknown,
                result.TimedOut ? "Rule inspection timed out" : "Rule inspection failed",
                FirstUsefulLine(result.StandardError));
        }

        var ruleNames = ReadRuleNames(result.StandardOutput);
        return ruleNames.Count > 0
            ? new ComponentHealth("Windows Firewall", ComponentState.Ready, $"{ruleNames.Count} enabled Sunshine inbound rule(s)", string.Join(", ", ruleNames))
            : new ComponentHealth("Windows Firewall", ComponentState.NeedsAttention, "No enabled Sunshine inbound application rule found", "Sunshine may prompt Windows to create a rule on first launch.");
    }

    public static IReadOnlyList<string> ReadRuleNames(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => [document.RootElement.GetString()!],
                JsonValueKind.Array => document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray(),
                _ => []
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FirstUsefulLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
        ?? "No detail was returned.";
}
