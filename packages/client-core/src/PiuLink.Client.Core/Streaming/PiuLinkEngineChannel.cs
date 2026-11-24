using System.IO.Pipes;
using System.Text;

namespace PiuLink.Client.Core.Streaming;

public sealed class PiuLinkEngineChannel : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private volatile bool _disposed;

    public PiuLinkEngineChannel(string? pipeName = null)
    {
        PipeName = pipeName ?? $"piulink-engine-{Environment.ProcessId}-{Guid.NewGuid():N}";
        if (PipeName.Length is < 1 or > 200 || PipeName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Pipe name contains unsupported characters.", nameof(pipeName));

        _pipe = new NamedPipeServerStream(
            PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public string PipeName { get; }
    public bool IsConnected => !_disposed && _pipe.IsConnected;

    public async Task WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await _pipe.WaitForConnectionAsync(timeoutCancellation.Token).ConfigureAwait(false);
            lock (_disposeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("PiuLink streaming engine did not connect to its control pipe.");
        }
    }

    public async Task SendAsync(PiuLinkEngineCommand command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _writer is null) throw new InvalidOperationException("PiuLink engine control pipe is not connected.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = lifetime.Token;
        var line = PiuLinkEngineProtocol.SerializeCommand(command);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer!.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async IAsyncEnumerable<PiuLinkEngineTelemetry> ReadTelemetryAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _reader is null) throw new InvalidOperationException("PiuLink engine control pipe is not connected.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = lifetime.Token;
        while (true)
        {
            var line = await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) yield break;
            if (line.Length > 64 * 1024) throw new InvalidDataException("Engine telemetry line exceeded 64 KiB.");
            yield return PiuLinkEngineProtocol.ParseTelemetry(line);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        _pipe.Dispose(); // A peer that stopped reading must not keep shutdown blocked on WriteAsync.
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try { _writer?.Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            _reader?.Dispose();
        }
        finally { _writeLock.Release(); }
        // Do not dispose this semaphore while already queued senders may still unwind.
        // It and the cancelled CTS have no unmanaged handles allocated and are collected with this channel.
    }
}
