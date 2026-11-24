using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace PiuLink.Host.Services;

/// <summary>Acquire and dispose on the application dispatcher thread (Windows mutex ownership).</summary>
internal sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    public bool IsPrimary { get; }

    // Shared by different installation directories and Windows sessions.
    public SingleInstanceService(string name = "ATIPiuLabs.PiuLink.Host")
    {
        _pipeName = name + ".Activate";
        _mutex = new Mutex(false, @"Global\" + name);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public void Listen(Action activate)
        => Listen((_, _) => activate());

    public void Listen(Action<byte, int> activate)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException();
        _listener = ListenAsync(activate);
    }

    private async Task ListenAsync(Action<byte, int> activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                // The newly launched foreground process grants the existing process activation rights.
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), timeout.Token).ConfigureAwait(false);
                var command = new byte[1];
                await pipe.ReadExactlyAsync(command, timeout.Token).ConfigureAwait(false);
                if (command[0] is >= 1 and <= 3)
                {
                    int appId = 0;
                    if (command[0] != 1) {
                        var id = new byte[4];
                        await pipe.ReadExactlyAsync(id, timeout.Token).ConfigureAwait(false);
                        appId = BitConverter.ToInt32(id);
                    }
                    activate(command[0], appId);
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException)
            {
                // A launcher may close before the handshake finishes, or pipe creation may fail transiently.
                try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            catch (UnauthorizedAccessException)
            {
                try { await Task.Delay(250, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    public async Task<bool> ActivateExistingAsync(byte command = 1, int appId = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            var process = new byte[4];
            await pipe.ReadExactlyAsync(process, timeout.Token);
            AllowSetForegroundWindow(BitConverter.ToInt32(process));
            await pipe.WriteAsync(new byte[] { command }, timeout.Token);
            if (command != 1) await pipe.WriteAsync(BitConverter.GetBytes(appId), timeout.Token);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, timeout.Token);
            return acknowledgement[0] == 1;
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            // Listener never synchronously waits for the UI thread.
            _listener?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            _stop.Dispose();
            if (IsPrimary) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
