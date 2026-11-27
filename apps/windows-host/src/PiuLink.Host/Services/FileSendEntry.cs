using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PiuLink.Host.Services;

internal static class FileSendEntry
{
    private static string QueuePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiuLink", "send-queue");
    public static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\PiuLink.Send");
        key.SetValue("", "发送到 PiuLink 手机");
        key.SetValue("MultiSelectModel", "Single");
        using var command = key.CreateSubKey("command");
        command.SetValue("", $"\"{Environment.ProcessPath}\" --send \"%1\"");
    }
    public static void Enqueue(string[] paths)
    {
        Directory.CreateDirectory(QueuePath);
        var target = Path.Combine(QueuePath, Guid.NewGuid().ToString("N") + ".json");
        var temporary = target + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(paths));
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string[] Drain()
    {
        if (!Directory.Exists(QueuePath)) return [];
        var result = new List<string>();
        foreach (var file in Directory.GetFiles(QueuePath, "*.json"))
        {
            try { result.AddRange((JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? []).Where(File.Exists)); File.Delete(file); }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return result.ToArray();
    }
}
