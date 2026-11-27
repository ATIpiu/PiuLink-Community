namespace PiuLink.Client.Core.Streaming;

public sealed record MoonlightEngineLocation(bool IsAvailable, string? ExecutablePath, string Detail);

public sealed class MoonlightEngineLocator
{
    public MoonlightEngineLocation Locate()
    {
        foreach (var candidate in GetCandidates().Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (File.Exists(fullPath))
            {
                return new MoonlightEngineLocation(true, fullPath, $"PiuLink native streaming core: {fullPath}");
            }
        }

        return new MoonlightEngineLocation(
            false,
            null,
            "PiuLink native streaming core is missing. Run native-engine/Build-NativeEngine.ps1 before publishing.");
    }

    private static IEnumerable<string?> GetCandidates()
    {
        yield return Environment.GetEnvironmentVariable("PIULINK_NATIVE_LIBRARY_PATH");
        yield return Path.Combine(AppContext.BaseDirectory, "native", "PiuLink.Native.dll");
    }
}
