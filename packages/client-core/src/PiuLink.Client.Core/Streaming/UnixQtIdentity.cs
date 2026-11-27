using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace PiuLink.Client.Core.Streaming;

internal static class UnixQtIdentity
{
    internal static (X509Certificate2 Certificate, byte[]? Pin) Read(string hostId, bool requirePair)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var values = OperatingSystem.IsMacOS() ? ReadMac(home) : ReadLinux(home);
        var group = values.GetValueOrDefault("hostsbackup/size", "0") != "0" ? "hostsbackup/" : "hosts/";
        var pins = values.Where(kv => kv.Key.StartsWith(group, StringComparison.Ordinal) &&
            kv.Key.EndsWith("/uuid", StringComparison.Ordinal) && kv.Value.Equals(hostId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => values.GetValueOrDefault(kv.Key[..^4] + "srvcert")).Where(p => !string.IsNullOrEmpty(p))
            .Select(p => { using var cert = X509Certificate2.CreateFromPem(p); return Convert.ToBase64String(cert.RawData); })
            .Distinct(StringComparer.Ordinal).ToArray();
        if (pins.Length > 1 || (requirePair && pins.Length != 1)) throw new IOException("无法确认 Moonlight 已配对的 Host 证书。");
        var pin = pins.Length == 1 ? Convert.FromBase64String(pins[0]) : null;
        if (values.TryGetValue("certificate", out var pem) && values.TryGetValue("key", out var key))
            return (X509Certificate2.CreateFromPem(pem, key), pin);
        if (pin != null || values.ContainsKey("certificate") || values.ContainsKey("key") || requirePair)
            throw new IOException("Moonlight 的配对身份不完整。");
        // A new Moonlight installation creates its permanent identity at first launch.
        // The Host must explicitly approve this first, unpaired invitation request.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PiuLink Invitation", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return (request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1)), null);
    }

    private static Dictionary<string, string> ReadLinux(string home)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config)) config = Path.Combine(home, ".config");
        var paths = new[] { config, Path.Combine(home,".var/app/com.moonlight_stream.Moonlight/config"),
            Path.Combine(home,"snap/moonlight/common/.config"), Path.Combine(home,"snap/moonlight/current/.config") }
            .Select(dir => Path.Combine(dir, "Moonlight Game Streaming Project/Moonlight.conf")).Distinct().Where(File.Exists).ToArray();
        if (paths.Length > 1) throw new IOException("发现多个 Moonlight 配置，请使用单一的 Moonlight 安装进行配对。");
        var values = new Dictionary<string,string>();
        if (paths.Length == 0) return values;
        var section = "";
        foreach (var line in File.ReadLines(paths[0]))
        {
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var equals = line.IndexOf('=');
            if (equals < 1 || section is not ("General" or "hosts" or "hostsbackup")) continue;
            var name = (section == "General" ? "" : section + "/") + line[..equals].Replace('\\','/');
            var value = line[(equals+1)..].Trim('"');
            if (value.StartsWith("@ByteArray(", StringComparison.Ordinal) && value.EndsWith(')')) value = value[11..^1];
            values[name] = value.Replace("\\n", "\n").Replace("\\r", "\r");
        }
        return values;
    }

    private static Dictionary<string, string> ReadMac(string home)
    {
        var values = new Dictionary<string, string>();
        var path = Path.Combine(home, "Library/Preferences/com.moonlight-stream.Moonlight.plist");
        if (!File.Exists(path)) return values;
        var start = new ProcessStartInfo("/usr/bin/plutil") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-convert", "xml1", "-o", "-", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法读取 Moonlight 配置。");
        var xml = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("无法读取 Moonlight 配置。");
        var dict = XDocument.Parse(xml).Root?.Element("dict") ?? throw new IOException("Moonlight 配置格式错误。");
        foreach (var key in dict.Elements("key"))
        {
            if (key.NextNode is not XElement value) continue;
            values[key.Value.Replace('.', '/')] = value.Name.LocalName == "data" ?
                Encoding.ASCII.GetString(Convert.FromBase64String(value.Value)) : value.Value;
        }
        return values;
    }
}
