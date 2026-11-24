using System.Text.Json;
using PiuLink.Host.Core.Models;

namespace PiuLink.Host.Core.Diagnostics;

public sealed class GpuProbe(IProcessRunner processRunner)
{
    public async Task<IReadOnlyList<GpuInfo>> CheckAsync(CancellationToken cancellationToken = default)
    {
        const string command = "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name | ConvertTo-Json -Compress";
        var result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            TimeSpan.FromSeconds(8),
            cancellationToken);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => [ToGpu(document.RootElement.GetString())],
                JsonValueKind.Array => document.RootElement
                    .EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => ToGpu(element.GetString()))
                    .ToArray(),
                _ => []
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static GpuVendor ClassifyVendor(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Nvidia;
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }

        return name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            ? GpuVendor.Intel
            : GpuVendor.Unknown;
    }

    private static GpuInfo ToGpu(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? "Unknown display adapter" : name.Trim();
        return new GpuInfo(normalized, ClassifyVendor(normalized));
    }
}

