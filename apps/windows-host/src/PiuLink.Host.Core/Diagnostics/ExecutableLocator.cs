namespace PiuLink.Host.Core.Diagnostics;

public static class ExecutableLocator
{
    public static string? FindTailscale() => FindFirst(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
        FindOnPath("tailscale.exe"));

    public static string? FindSunshine() => FindFirst(
        Path.Combine(AppContext.BaseDirectory, "engine", "sunshine.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ATIPiu Labs", "PiuLink Host", "engine", "sunshine.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine", "sunshine.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LizardByte", "Sunshine", "sunshine.exe"),
        FindOnPath("sunshine.exe"));

    private static string? FindFirst(params string?[] paths) => paths.FirstOrDefault(path =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH segments.
            }
        }

        return null;
    }
}
