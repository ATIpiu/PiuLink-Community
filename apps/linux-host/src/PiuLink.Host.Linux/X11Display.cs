using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PiuLink.Host.Linux;

internal sealed record X11Display(int CaptureIndex, string DeviceId, string Name, bool IsPrimary, bool IsVirtual,
    int Width, int Height, int RefreshRate, X11Display.Rectangle Bounds)
{
    internal sealed record Rectangle(int X, int Y, int Width, int Height);
    private static readonly Regex Output = new(@"^(\S+) (connected|disconnected|unknown connection)\b(.*)$");
    private static readonly Regex Geometry = new(@"\b(\d+)x(\d+)([+-]\d+)([+-]\d+)\b");
    private static readonly Regex CurrentRate = new(@"(\d+(?:\.\d+)?)\*");

    internal static async Task<X11Display?> ReadAsync(CancellationToken token)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        var configured = Environment.GetEnvironmentVariable("PIULINK_OUTPUT_INDEX");
        int? outputIndex = null;
        if (!string.IsNullOrEmpty(configured))
        {
            if (!int.TryParse(configured, out var index) || index < 0) throw new ArgumentException("Invalid capture output index.");
            outputIndex = index;
        }
        var start = new ProcessStartInfo("xrandr", "--query") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("Could not run xrandr.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await stderr;
            if (process.ExitCode != 0) return null;
            return Select(await stdout, outputIndex, Environment.GetEnvironmentVariable("PIULINK_SESSION_MODE") == "headless");
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
    internal static X11Display? Select(string query, int? outputIndex, bool headless)
    {
        var displays = new List<X11Display>();
        int index = -1;
        bool active = false;
        foreach (var line in query.Split('\n'))
        {
            var output = Output.Match(line);
            if (output.Success)
            {
                // Sunshine counts every XRandR output, including disconnected connectors.
                index++;
                var properties = output.Groups[3].Value;
                var geometry = Geometry.Match(properties);
                active = output.Groups[2].Value == "connected" && geometry.Success;
                if (!active) continue;
                var name = output.Groups[1].Value;
                var width = int.Parse(geometry.Groups[1].Value);
                var height = int.Parse(geometry.Groups[2].Value);
                displays.Add(new(index, "linux-x11-" + name, headless ? "PiuLink Headless Desktop" : name,
                    headless || properties.Contains(" primary "), headless, width, height, 60,
                    new(int.Parse(geometry.Groups[3].Value), int.Parse(geometry.Groups[4].Value), width, height)));
            }
            else if (active)
            {
                var rate = CurrentRate.Match(line);
                if (rate.Success && double.TryParse(rate.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var hz) && hz >= 10)
                    displays[^1] = displays[^1] with { RefreshRate = (int)Math.Round(hz) };
            }
        }
        return outputIndex.HasValue ? displays.Find(d => d.CaptureIndex == outputIndex.Value)
            : displays.Find(d => d.IsPrimary) ?? displays.FirstOrDefault();
    }

}
