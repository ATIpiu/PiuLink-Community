using System.Diagnostics;

namespace PiuLink.Host.Core.Diagnostics;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new ProcessResult(-1, string.Empty, $"Unable to start {fileName}.", false);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(linkedSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(linkedSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr, false);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process already exited. */ }
            catch (System.ComponentModel.Win32Exception) { /* Access denied or exit race; don't mask cancellation. */ }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(reap.Token); } catch (OperationCanceledException) { }
            // Readers were given the same cancellation token, not an unrelated caller-only token.
            var readers = Task.WhenAll(stdout, stderr);
            try { await readers.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException) { }
            // Observe a late reader failure after the bounded wait; Process.Dispose closes its local pipe handles.
            _ = readers.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty,
                stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty, true);
        }
    }
}
