namespace PiuLink.Mac;

internal static class DesktopPlatform
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    internal static string Name => OperatingSystem.IsLinux() ? "Linux" : "Mac";
    internal static string Config => OperatingSystem.IsLinux()
        ? Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "piulink")
        : Path.Combine(Home, "Library", "Application Support", "PiuLink");
    internal static string Logs => OperatingSystem.IsLinux()
        ? Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), "piulink", "logs")
        : Path.Combine(Home, "Library", "Logs", "PiuLink");

    private static string Xdg(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is string path && Path.IsPathFullyQualified(path)
            ? path : Path.Combine(Home, fallback);

    internal static string FindEngine()
    {
        var custom = Environment.GetEnvironmentVariable("PIULINK_MOONLIGHT_PATH");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (Path.IsPathFullyQualified(dir) && File.Exists(Path.Combine(dir, "moonlight")))
                    return Path.Combine(dir, "moonlight");
            string bundled = Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "piulink-linux/runtime/moonlight-6.2.0/AppRun");
            return File.Exists(bundled) ? bundled : "/usr/bin/moonlight";
        }
        const string bundle = "Moonlight.app/Contents/MacOS/Moonlight";
        var local = Path.Combine(Home, "Applications", bundle);
        return File.Exists(local) ? local : Path.Combine("/Applications", bundle);
    }
}
