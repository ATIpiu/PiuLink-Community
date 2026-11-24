using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace PiuLink.Client;

internal enum ClientLogLevel { Debug = 0, Info = 1, Err = 2 }

internal static class ClientLog
{
    // Global diagnostics policy: code-only by design, no UI setting.
    private const ClientLogLevel MinimumLevel = ClientLogLevel.Info;
    private const int RetentionDays = 3;
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const long MaxTotalBytes = 48L * 1024 * 1024;
    private const int QueueCapacity = 4096;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);
    private sealed record Entry(DateTimeOffset Time, ClientLogLevel Level, string Event, string Module, string Message,
        string RunId, string? OperationId, string SourceFile, int SourceLine, string Member, int ThreadId,
        IReadOnlyDictionary<string, object?>? Fields, Exception? Exception);

    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly Channel<Entry> Queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(QueueCapacity)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private static readonly object FileWriteLock = new();
    private static long _dropped;
    private static DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;
    private static string? _currentPath;
    private static int _segment;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATIPiu Labs", "PiuLink Client", "logs");

    private static readonly Task Writer;
    internal static void Drain() { Queue.Writer.TryComplete(); Writer.GetAwaiter().GetResult(); }

    static ClientLog()
    {
        Directory.CreateDirectory(DirectoryPath);
        Cleanup();
        Writer = Task.Run(WriterLoopAsync);
        _ = InfoAsync("diag.start", "diagnostics", "diagnostics logger started",
            fields: new Dictionary<string, object?> { ["retention_days"] = RetentionDays, ["minimum_level"] = MinimumLevel.ToString() });
    }

    public static Task WriteAsync(string message, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string member = "")
    {
        var looksLikeError = message.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("exception", StringComparison.OrdinalIgnoreCase);
        return looksLikeError
            ? ErrorAsync("legacy.error", "legacy", message, sourceFile: sourceFile, sourceLine: sourceLine, member: member)
            : InfoAsync("legacy.message", "legacy", message, sourceFile: sourceFile, sourceLine: sourceLine, member: member);
    }

    public static Task InfoAsync(string eventId, string module, string message, string? operationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string member = "") => Enqueue(ClientLogLevel.Info, eventId, module, message, operationId, fields, null, sourceFile, sourceLine, member);

    public static Task ErrorAsync(string eventId, string module, string message, Exception? exception = null, string? operationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string member = "") => Enqueue(ClientLogLevel.Err, eventId, module, message, operationId, fields, exception, sourceFile, sourceLine, member);

    public static void ErrorSync(string eventId, string module, string message, Exception? exception = null, string? operationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string member = "")
    {
        try
        {
            var entry = new Entry(DateTimeOffset.UtcNow, ClientLogLevel.Err, eventId, module, Sanitize(message), RunId, operationId,
                Path.GetFileName(sourceFile), sourceLine, member, Environment.CurrentManagedThreadId, fields, exception);
            AppendEntrySync(entry);
        }
        catch { }
    }

    public static Task DebugAsync(string eventId, string module, string message, string? operationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string member = "") => Enqueue(ClientLogLevel.Debug, eventId, module, message, operationId, fields, null, sourceFile, sourceLine, member);

    private static Task Enqueue(ClientLogLevel level, string eventId, string module, string message, string? operationId,
        IReadOnlyDictionary<string, object?>? fields, Exception? exception, string sourceFile, int sourceLine, string member)
    {
        if (level < MinimumLevel) return Task.CompletedTask;
        var entry = new Entry(DateTimeOffset.UtcNow, level, eventId, module, Sanitize(message), RunId, operationId,
            Path.GetFileName(sourceFile), sourceLine, member, Environment.CurrentManagedThreadId, fields, exception);
        if (!Queue.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
        return Task.CompletedTask;
    }

    private static async Task WriterLoopAsync()
    {
        await foreach (var entry in Queue.Reader.ReadAllAsync())
        {
            try
            {
                AppendEntrySync(entry);
            }
            catch { }
        }
    }

    private static void AppendEntrySync(Entry entry)
    {
        lock (FileWriteLock)
        {
            if (DateTimeOffset.UtcNow >= _nextCleanup) Cleanup();
            var path = CurrentPath(entry.Time.UtcDateTime);
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            var payload = new Dictionary<string, object?>
            {
                ["ts"] = entry.Time.ToString("O"), ["level"] = entry.Level.ToString().ToUpperInvariant(),
                ["event"] = entry.Event, ["module"] = entry.Module, ["message"] = entry.Message,
                ["run_id"] = entry.RunId, ["operation_id"] = entry.OperationId,
                ["source"] = entry.SourceFile, ["line"] = entry.SourceLine, ["member"] = entry.Member,
                ["thread"] = entry.ThreadId, ["fields"] = entry.Fields, ["dropped_before"] = dropped == 0 ? null : dropped
            };
            if (entry.Exception is not null) payload["exception"] = new Dictionary<string, object?>
            { ["type"] = entry.Exception.GetType().FullName, ["message"] = Sanitize(entry.Exception.Message), ["stack"] = Limit(Sanitize(entry.Exception.StackTrace ?? ""), 12000) };
            File.AppendAllText(path, JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
    }

    private static string CurrentPath(DateTime utc)
    {
        var prefix = $"piulink-client-{utc:yyyyMMdd}";
        if (_currentPath is null || !Path.GetFileName(_currentPath).StartsWith(prefix, StringComparison.Ordinal) ||
            (File.Exists(_currentPath) && new FileInfo(_currentPath).Length >= MaxFileBytes))
        {
            if (_currentPath is null || !Path.GetFileName(_currentPath).StartsWith(prefix, StringComparison.Ordinal)) _segment = 0;
            do { _currentPath = Path.Combine(DirectoryPath, $"{prefix}-{_segment++:00}.jsonl"); }
            while (File.Exists(_currentPath) && new FileInfo(_currentPath).Length >= MaxFileBytes);
        }
        return _currentPath;
    }

    private static void Cleanup()
    {
        Directory.CreateDirectory(DirectoryPath);
        _nextCleanup = DateTimeOffset.UtcNow + CleanupInterval;
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var path in Directory.GetFiles(DirectoryPath, "*.jsonl")) try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); } catch { }
        var files = Directory.GetFiles(DirectoryPath, "*.jsonl").Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc)) { if (total <= MaxTotalBytes) break; try { total -= file.Length; file.Delete(); } catch { } }
    }

    public static string ReadRecent()
    {
        const int limit = 256 * 1024;
        int remaining = limit;
        var sections = new List<string>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc)) {
            if (remaining == 0) break;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int count = (int)Math.Min(remaining, file.Length);
            file.Seek(-count, SeekOrigin.End);
            using var reader = new StreamReader(file);
            if (file.Position > 0) reader.ReadLine();
            sections.Add(Path.GetFileName(path) + Environment.NewLine + reader.ReadToEnd());
            remaining -= count;
        }
        sections.Reverse();
        return string.Join(Environment.NewLine, sections);
    }

    public static void Clear() { Directory.CreateDirectory(DirectoryPath); foreach (var path in Directory.GetFiles(DirectoryPath, "*.jsonl")) try { File.Delete(path); } catch { } }
    private static string Sanitize(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
