using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace PiuLink.Host.Core.Diagnostics;

public sealed class LocalLog : IDisposable
{
    private sealed record Entry(
        DateTimeOffset Time,
        DiagnosticLevel Level,
        string Event,
        string Module,
        string Message,
        string RunId,
        string? OperationId,
        string SourceFile,
        int SourceLine,
        string Member,
        int ThreadId,
        IReadOnlyDictionary<string, object?>? Fields,
        Exception? Exception);

    private readonly string _directory;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly Channel<Entry> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;
    private long _dropped;
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;
    private int _segment;
    private string? _currentPath;

    public LocalLog(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ATIPiu Labs", "PiuLink Host", "logs");
        Directory.CreateDirectory(_directory);
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(DiagnosticPolicy.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        Cleanup();
        _writer = Task.Run(WriterLoopAsync);
        _ = InfoAsync("diag.start", "diagnostics", "diagnostics logger started",
            fields: new Dictionary<string, object?> { ["retention_days"] = DiagnosticPolicy.RetentionDays, ["minimum_level"] = DiagnosticPolicy.MinimumLevel.ToString() });
    }

    public string DirectoryPath => _directory;
    public string RunId => _runId;

    public Task WriteAsync(string message, CancellationToken cancellationToken = default,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string member = "")
    {
        var looksLikeError = message.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("exception", StringComparison.OrdinalIgnoreCase);
        return looksLikeError
            ? ErrorAsync("legacy.error", "legacy", message, cancellationToken: cancellationToken, sourceFile: sourceFile, sourceLine: sourceLine, member: member)
            : InfoAsync("legacy.message", "legacy", message, cancellationToken: cancellationToken, sourceFile: sourceFile, sourceLine: sourceLine, member: member);
    }

    public Task InfoAsync(string eventId, string module, string message, string? operationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, CancellationToken cancellationToken = default,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string member = "") =>
        EnqueueAsync(DiagnosticLevel.Info, eventId, module, message, operationId, fields, null, cancellationToken, sourceFile, sourceLine, member);

    public Task ErrorAsync(string eventId, string module, string message, Exception? exception = null, string? operationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, CancellationToken cancellationToken = default,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string member = "") =>
        EnqueueAsync(DiagnosticLevel.Err, eventId, module, message, operationId, fields, exception, cancellationToken, sourceFile, sourceLine, member);

    public Task DebugAsync(string eventId, string module, string message, string? operationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, CancellationToken cancellationToken = default,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string member = "") =>
        EnqueueAsync(DiagnosticLevel.Debug, eventId, module, message, operationId, fields, null, cancellationToken, sourceFile, sourceLine, member);

    private Task EnqueueAsync(DiagnosticLevel level, string eventId, string module, string message, string? operationId,
        IReadOnlyDictionary<string, object?>? fields, Exception? exception, CancellationToken cancellationToken,
        string sourceFile, int sourceLine, string member)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (level < DiagnosticPolicy.MinimumLevel) return Task.CompletedTask;
        var entry = new Entry(DateTimeOffset.UtcNow, level, eventId, module, Sanitize(message), _runId, operationId,
            Path.GetFileName(sourceFile), sourceLine, member, Environment.CurrentManagedThreadId, fields, exception);
        if (!_queue.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
        return Task.CompletedTask;
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    if (DateTimeOffset.UtcNow >= _nextCleanup) Cleanup();
                    await WriteEntryAsync(entry).ConfigureAwait(false);
                }
                catch { /* Diagnostics must never take down the app. */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteEntryAsync(Entry entry)
    {
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
        if (entry.Exception is not null)
        {
            payload["exception"] = new Dictionary<string, object?>
            {
                ["type"] = entry.Exception.GetType().FullName,
                ["message"] = Sanitize(entry.Exception.Message),
                ["stack"] = Limit(Sanitize(entry.Exception.StackTrace ?? string.Empty), 12000)
            };
        }
        var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line).ConfigureAwait(false);
    }

    private string CurrentPath(DateTime utc)
    {
        var prefix = $"piulink-host-{utc:yyyyMMdd}";
        if (_currentPath is null || !Path.GetFileName(_currentPath).StartsWith(prefix, StringComparison.Ordinal) ||
            (File.Exists(_currentPath) && new FileInfo(_currentPath).Length >= DiagnosticPolicy.MaxFileBytes))
        {
            if (_currentPath is null || !Path.GetFileName(_currentPath).StartsWith(prefix, StringComparison.Ordinal)) _segment = 0;
            do { _currentPath = Path.Combine(_directory, $"{prefix}-{_segment++:00}.jsonl"); }
            while (File.Exists(_currentPath) && new FileInfo(_currentPath).Length >= DiagnosticPolicy.MaxFileBytes);
        }
        return _currentPath;
    }

    private void Cleanup()
    {
        Directory.CreateDirectory(_directory);
        _nextCleanup = DateTimeOffset.UtcNow + DiagnosticPolicy.CleanupInterval;
        var cutoff = DateTime.UtcNow.AddDays(-DiagnosticPolicy.RetentionDays);
        foreach (var path in Directory.GetFiles(_directory, "*.jsonl"))
            try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); } catch { }
        var files = Directory.GetFiles(_directory, "*.jsonl").Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= DiagnosticPolicy.MaxTotalBytes) break;
            try { total -= file.Length; file.Delete(); } catch { }
        }
    }

    public string ReadAll()
    {
        Directory.CreateDirectory(_directory);
        return string.Join(Environment.NewLine, Directory.GetFiles(_directory, "*.jsonl").OrderBy(File.GetLastWriteTimeUtc)
            .Select(path => $"—— {Path.GetFileName(path)} ——{Environment.NewLine}{ReadFile(path)}"));
    }

    public void Clear()
    {
        Directory.CreateDirectory(_directory);
        foreach (var path in Directory.GetFiles(_directory, "*.jsonl")) try { File.Delete(path); } catch { }
    }

    private static string ReadFile(string path) { try { return File.ReadAllText(path); } catch (Exception e) { return "Unable to read log: " + e.Message; } }
    private static string Sanitize(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.GetAwaiter().GetResult();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
