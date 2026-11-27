using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PiuLink.Updates;

internal sealed record UpdateChannel(string Id, string Installed, string? OriginalInstaller = null);
internal sealed record UpdateInfo([property: JsonPropertyName("version")] string Version, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("bytes")] long Bytes);
internal sealed record DownloadResult([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("downloaded")] long Downloaded, [property: JsonPropertyName("reused")] long Reused);

internal static class ReleaseDownload
{
    internal static UpdateChannel Channel(bool host)
    {
        string version = Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3);
        if (OperatingSystem.IsWindows())
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var products = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (products == null) continue;
                foreach (string id in products.GetSubKeyNames())
                {
                    using var product = products.OpenSubKey(id);
                    if (product?.GetValue("DisplayName") as string == "PiuLink Windows" &&
                        product.GetValue("DisplayVersion") is string suiteVersion)
                    {
                        using var variables = product.OpenSubKey("variables");
                        return new("win-suite", suiteVersion, variables?.GetValue("WixBundleOriginalSource") as string);
                    }
                }
            }
            return new(host ? "win-host" : "win-client", version);
        }
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsMacOS())
        {
            string sibling = arch == "arm64" ? "osx-x64" : "osx-arm64";
            bool universal = File.Exists(Path.Combine(AppContext.BaseDirectory, "..", sibling, "PiuLink.Mac"));
            return new(universal ? "mac-universal" : $"mac-{arch}", version);
        }
        string other = arch == "arm64" ? "linux-x64" : "linux-arm64";
        if (!host && File.Exists(Path.Combine(AppContext.BaseDirectory, "..", other, "PiuLink.Linux")))
            return new("linux-universal", version);
        string channel = host ? "linux-host" : $"linux-{arch}";
        string packaged = host ? "/usr/lib/piulink-host/" : "/usr/lib/piulink-client/";
        if (!AppContext.BaseDirectory.StartsWith(packaged, StringComparison.Ordinal)) channel += "-portable";
        else if (File.Exists("/usr/share/piulink-suite/version"))
        {
            string suite = File.ReadAllText("/usr/share/piulink-suite/version").Trim();
            if (!Version.TryParse(suite, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0)
                throw new InvalidDataException("整合版版本信息损坏，请修复安装。");
            return new("linux-suite", suite);
        }
        return new(channel, version);
    }

    internal static async Task<UpdateInfo?> CheckAsync(UpdateChannel channel) =>
        JsonSerializer.Deserialize<UpdateInfo>(await RunAsync("check", channel.Id, channel.Installed));

    internal static async Task<DownloadResult> PrepareAsync(UpdateChannel channel, UpdateInfo update, string? previous = null)
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiuLink", "Updates");
        string oldName = update.Name.Replace(update.Version, channel.Installed, StringComparison.Ordinal);
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", oldName);
        string seed = previous ?? "";
        if (previous == null)
        {
            string? original = channel.OriginalInstaller;
            // Burn records the original full EXE even when it was renamed or saved outside Downloads.
            // Drive-letter paths on a local disk avoid waking mapped network drives or probing UNC shares.
            if (OperatingSystem.IsWindows() && original is { Length: > 3 } &&
                char.IsAsciiLetter(original[0]) && original[1] == ':' && original[2] == Path.DirectorySeparatorChar &&
                original.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                new DriveInfo(original[..3]).DriveType == DriveType.Fixed && File.Exists(original))
                seed = original;
            else if (File.Exists(downloads))
                seed = downloads;
            else if (OperatingSystem.IsLinux() && oldName.EndsWith(".deb", StringComparison.Ordinal))
            {
                string aptArchive = Path.Combine("/var/cache/apt/archives", oldName);
                if (File.Exists(aptArchive)) seed = aptArchive;
            }
        }
        return JsonSerializer.Deserialize<DownloadResult>(await RunAsync("prepare", channel.Id, channel.Installed, cache, seed))
            ?? throw new IOException("更新包未就绪。");
    }

    private static async Task<string> RunAsync(params string[] args)
    {
        string binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "piulink-direct.exe" : "piulink-direct");
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("update");
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new IOException("无法启动更新组件。");
        string result = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            using var failure = JsonDocument.Parse(result);
            throw new IOException(failure.RootElement.GetProperty("message").GetString());
        }
        return result;
    }
}
