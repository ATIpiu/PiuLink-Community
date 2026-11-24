using System.Text;

namespace PiuLink.Mac;

internal static class LocalLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string DirectoryPath => DesktopPlatform.Logs;

    public static async Task WriteAsync(string message)
    {
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await File.AppendAllTextAsync(Path.Combine(DirectoryPath, $"piulink-{DesktopPlatform.Name.ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
            foreach (var old in Directory.GetFiles(DirectoryPath, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(7))
                try { File.Delete(old); } catch { }
        }
        catch { }
        finally { Gate.Release(); }
    }

    public static string ReadAll()
    {
        Directory.CreateDirectory(DirectoryPath);
        return string.Join(Environment.NewLine,
            Directory.GetFiles(DirectoryPath, "*.log").OrderBy(File.GetLastWriteTimeUtc)
                .Select(path => $"—— {Path.GetFileName(path)} ——{Environment.NewLine}{File.ReadAllText(path)}"));
    }

    public static void Clear()
    {
        Directory.CreateDirectory(DirectoryPath);
        foreach (var path in Directory.GetFiles(DirectoryPath, "*.log")) try { File.Delete(path); } catch { }
    }
}
