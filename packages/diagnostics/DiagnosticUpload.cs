using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PiuLink.Diagnostics;

internal static class DiagnosticUpload
{
    private const string Endpoint = "https://atipiu.org:8443/piulink/v1/diagnostics";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(18) };
    private static readonly Regex Symbol = new(@"^[A-Za-z_$][A-Za-z0-9_.$]{0,158}$");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly Lazy<HashSet<string>> Codes = new(() => {
        using var stream = typeof(DiagnosticUpload).Assembly.GetManifestResourceStream("PiuLink.DiagnosticEvents")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    });
    private static readonly string Version = typeof(DiagnosticUpload).Assembly.GetName().Version!.ToString(3);
    private sealed record Event(long Time, string Code, string? Symbol = null, int? Value = null, string? Build = null, string? Reason = null);
    private static string directory = "", platform = "";
    private const string CrashName = "diagnostic-crash.json";

    public static void Initialize(string logDirectory, string app)
    {
        directory = logDirectory;
        platform = app;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            if (args.ExceptionObject is Exception error) Capture(error);
        };
    }

    public static void Capture(Exception error)
    {
        try {
            long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var events = new List<Event>();
            for (Exception? cause = error; cause != null && events.Count < 20; cause = cause.InnerException) {
                string? name = cause.GetType().FullName;
                events.Add(new(time, "crash.exception", name != null && Symbol.IsMatch(name) ? name : null, Build: Version));
                foreach (var frame in new StackTrace(cause, true).GetFrames().Take(10)) {
                    var method = frame.GetMethod();
                    var site = method?.DeclaringType?.FullName + "." + method?.Name;
                    site = site.Replace('+', '.').Replace('<', '_').Replace('>', '_');
                    if (Symbol.IsMatch(site)) events.Add(new(time, "crash.frame", site, frame.GetFileLineNumber()));
                }
            }
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, CrashName);
            var temp = file + "." + Environment.ProcessId + ".tmp";
            lock (Json) {
                File.WriteAllText(temp, JsonSerializer.Serialize(events, Json));
                File.Move(temp, file, true);
            }
        } catch { /* The application's original exception handling must still run. */ }
    }

    private const string Connections = "diagnostic-connections.json";

    public static void ConnectionFailure(string stage, Exception? error = null, int? value = null)
    {
        string code = "connect." + stage;
        if (directory.Length == 0 || !Codes.Value.Contains(code)) return;
        string reason = "unknown";
        for (var cause = error; cause != null; cause = cause.InnerException) {
            reason = cause switch {
                TimeoutException or OperationCanceledException => "timeout",
                System.Security.Authentication.AuthenticationException => "tls",
                System.Net.Sockets.SocketException socket => socket.SocketErrorCode switch {
                    System.Net.Sockets.SocketError.TimedOut => "timeout",
                    System.Net.Sockets.SocketError.HostNotFound => "dns",
                    System.Net.Sockets.SocketError.ConnectionRefused => "refused",
                    _ => "unreachable" },
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "offline",
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "dns",
                HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "tls",
                HttpRequestException => "unreachable",
                _ => reason };
        }
        try {
            lock (Json) {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, Connections);
                var rows = SavedConnections(file);
                long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var last = rows.LastOrDefault();
                if (last?.Code == code && last.Reason == reason && last.Value == value && time - last.Time < 60) return;
                rows.Add(new(time, code, Value: value, Reason: reason));
                var pending = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(pending, JsonSerializer.Serialize(rows.TakeLast(8), Json));
                File.Move(pending, file, true);
            }
        } catch { /* A diagnostic failure must not interrupt a connection attempt. */ }
    }

    private static List<Event> SavedConnections(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.LinkTarget != null || info.Length > 4096) return [];
        try {
            var stored = JsonSerializer.Deserialize<List<Event>>(File.ReadAllText(file), Json) ?? [];
            return stored.Where(e => e.Code.StartsWith("connect.", StringComparison.Ordinal) && Codes.Value.Contains(e.Code)
                && e.Reason is "timeout" or "tls" or "dns" or "refused" or "offline" or "unreachable" or "unknown")
                .TakeLast(8).Select(e => new Event(e.Time, e.Code, Value:e.Value, Reason:e.Reason)).ToList();
        } catch (JsonException) { return []; }
    }

    private static Event? Project(string line, bool plain)
    {
        if (plain) {
            var space = line.IndexOf(' ');
            if (space < 1 || !DateTimeOffset.TryParse(line[..space], out var stamp)) return null;
            var message = line[(space + 1)..];
            var code = message.StartsWith("Operation failed: ", StringComparison.Ordinal) ||
                       message.StartsWith("Could not save stream preferences: ", StringComparison.Ordinal) ? "app.error" :
                       message.EndsWith(" client started", StringComparison.Ordinal) ? "app.start" : null;
            return code == null ? null : new(stamp.ToUnixTimeSeconds(), code);
        }
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ts", out var ts) ||
                ts.ValueKind != JsonValueKind.String || !ts.TryGetDateTimeOffset(out var date) ||
                !root.TryGetProperty("event", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var code = id.GetString()!;
            return new(date.ToUnixTimeSeconds(), Codes.Value.Contains(code) ? code : "app.event");
        } catch (JsonException) { return null; }
    }

    private static List<Event> Collect()
    {
        var recent = new Queue<Event>();
        bool plain = platform is "macos-client" or "linux-client";
        var pattern = plain ? "piulink-*.log" : platform.EndsWith("-host", StringComparison.Ordinal) ? "piulink-host-*.jsonl" : "piulink-client-*.jsonl";
        if (!Directory.Exists(directory)) return [];
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern)
                     .Where(f => f.LinkTarget == null).OrderBy(f => f.LastWriteTimeUtc).TakeLast(2)) {
            using var data = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Snapshot a bounded tail even when the log writer is still appending.
            var tail = new byte[(int)Math.Min(65536L, data.Length)];
            var start = data.Length - tail.Length;
            data.Position = start;
            int length = data.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false);
            foreach (var line in Encoding.UTF8.GetString(tail, 0, length).Split('\n').Skip(start > 0 ? 1 : 0)) {
                var item = Project(line.TrimStart('\uFEFF'), plain);
                if (item == null) continue;
                if (recent.Count == 20) recent.Dequeue();
                recent.Enqueue(item);
            }
        }
        var events = recent.ToList();
        events.AddRange(SavedConnections(Path.Combine(directory, Connections)));
        var crash = new FileInfo(Path.Combine(directory, CrashName));
        if (crash.Exists && crash.LinkTarget == null && crash.Length <= 24000) {
            var saved = JsonSerializer.Deserialize<List<Event>>(File.ReadAllText(crash.FullName), Json);
            if (saved != null) events.AddRange(saved.Where(e => e.Code is "crash.exception" or "crash.frame"
                && (e.Symbol == null || Symbol.IsMatch(e.Symbol))
                && (e.Build == null || Regex.IsMatch(e.Build, @"^\d{1,4}\.\d{1,4}\.\d{1,5}$"))).Take(40));
        }
        return events;
    }

    public static async Task<string> SendAsync()
    {
        var events = await Task.Run(Collect).ConfigureAwait(false);
        if (events.Count == 0) throw new InvalidOperationException("没有可上传的诊断记录。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, platform, version = Version, events }, Json);
        if (payload.Length > 16384) throw new IOException("摘要超出上传大小限制。");
        using var body = new ByteArrayContent(payload);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await Http.PostAsync(Endpoint, body).ConfigureAwait(false);
        if ((int)response.StatusCode == 429) throw new IOException("提交过于频繁，请稍后重试。");
        if ((int)response.StatusCode != 201) throw new IOException("服务器未确认接收，请稍后重试。");
        var id = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!Regex.IsMatch(id, "^[a-f0-9]{20}\\z")) throw new IOException("服务器回执无效。");
        return id;
    }
}
