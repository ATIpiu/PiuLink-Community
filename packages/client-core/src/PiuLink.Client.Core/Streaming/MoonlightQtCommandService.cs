using System.Diagnostics;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;

namespace PiuLink.Client.Core.Streaming;

public sealed record MoonlightQtEngineLocation(bool IsAvailable, string? ExecutablePath, string Detail);

/// <summary>
/// Locates the complete PiuLink runtime packaged with the WPF client.
/// Its pairing certificate and application list are intentionally kept in the
/// Qt engine, rather than sharing the managed in-process streaming identity.
/// </summary>
public sealed class MoonlightQtEngineLocator
{
    public MoonlightQtEngineLocation Locate()
    {
        foreach (var candidate in GetCandidates().Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (File.Exists(fullPath))
            {
                return new MoonlightQtEngineLocation(true, fullPath, "PiuLink pairing engine is ready.");
            }
        }

        return new MoonlightQtEngineLocation(
            false,
            null,
            "PiuLink pairing engine is missing. Install the complete PiuLink Client package.");
    }

    private static IEnumerable<string?> GetCandidates()
    {
        yield return Environment.GetEnvironmentVariable("PIULINK_MOONLIGHT_QT_EXE_PATH");
        yield return Path.Combine(AppContext.BaseDirectory, "moonlight-qt", "PiuLink-Stream.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "moonlight-qt", "PiuLink-Moonlight.exe");
    }
}

public sealed class MoonlightQtCommandService
{
    public MoonlightQtCommandService(string executablePath)
    {
        ExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException("PiuLink executable was not found.", ExecutablePath);
        }
    }

    public string ExecutablePath { get; }

    public Task<MoonlightCommandResult> PairAsync(
        HostEndpoint endpoint,
        string pin,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        OperatingSystem.IsMacOS() && Path.GetFileName(ExecutablePath) == "PiuLinkStream"
            ? PairWithPipeAsync(endpoint, pin, timeout, cancellationToken)
            : ExecuteAsync(MoonlightCommandBuilder.Pair(endpoint, pin), timeout, cancellationToken);

    private Task<MoonlightCommandResult> PairWithPipeAsync(
        HostEndpoint endpoint, string pin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var arguments = MoonlightCommandBuilder.Pair(endpoint, pin).ToArray();
        arguments[2] = "stdin";
        return ExecuteCoreAsync(arguments, timeout, cancellationToken, pin);
    }

    public Task<MoonlightCommandResult> ListAppsAsync(
        HostEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(MoonlightCommandBuilder.ListApps(endpoint), timeout, cancellationToken);

    public Task<MoonlightCommandResult> QuitAsync(HostEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        ExecuteAsync(MoonlightCommandBuilder.Quit(endpoint), timeout, cancellationToken);

    public async Task<MoonlightQtStreamSession> LaunchStreamAsync(
        HostEndpoint endpoint,
        string appName,
        StreamProfile profile,
        CancellationToken cancellationToken = default)
    {
        var channel = new PiuLinkEngineChannel();
        MoonlightQtStreamSession? session = null;
        try
        {
            var arguments = MoonlightCommandBuilder.Stream(endpoint, appName, profile).ToList();
            if (string.Equals(Environment.GetEnvironmentVariable("PIULINK_DEBUG_DISABLE_VSYNC"), "1", StringComparison.Ordinal))
            {
                // Diagnostic-only renderer baseline. It is inserted before
                // host/app positional arguments and never persists a Qt pref.
                arguments.InsertRange(1, ["--no-vsync", "--no-frame-pacing", "--video-decoder", "hardware"]);
            }
            var codecPolicy = AutoCodecPolicy.Decide(endpoint, profile);
            var process = StartInteractiveProcess(arguments, channel.PipeName, profile.ColorRange, codecPolicy,
                GameStreamHttpClient.PreparedRequests.GetValueOrDefault(endpoint.Host, ""));
            session = new MoonlightQtStreamSession(process, channel);
            // Give process startup failures a chance to surface before WPF
            // reports the native stream window as launched.
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw new IOException($"PiuLink exited before opening the stream window ({session.ExitCode}).");
            }

            // The integrated Qt engine opens this pipe after its streaming
            // session starts. WPF uses it for actual stats and adaptive bitrate,
            // rather than presenting stale settings as if they were live.
            await channel.WaitForConnectionAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    // Stock Moonlight.app supports Qt command-line options but has no PiuLink telemetry pipe.
    public Process LaunchExternalStream(HostEndpoint endpoint, string app, StreamProfile profile) =>
        StartInteractiveProcess(MoonlightCommandBuilder.Stream(endpoint, app, profile, integratedEngine: false), null, profile.ColorRange,
            AutoCodecPolicy.Decide(endpoint, profile), GameStreamHttpClient.PreparedRequests.GetValueOrDefault(endpoint.Host, ""));

    public Task<MoonlightCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(arguments, timeout, cancellationToken, null);
    }

    private async Task<MoonlightCommandResult> ExecuteCoreAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? pairingPin)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 || arguments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("PiuLink command arguments must not be empty.", nameof(arguments));
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = pairingPin != null
            }
        };
        process.StartInfo.Environment["PIULINK_QT_HEADLESS"] = "1";
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("PiuLink command could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            if (pairingPin != null)
            {
                await process.StandardInput.WriteLineAsync(pairingPin).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return new MoonlightCommandResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            return new MoonlightCommandResult(
                -1,
                await outputTask.ConfigureAwait(false),
                $"PiuLink command timed out after {timeout.TotalSeconds:0} seconds.{Environment.NewLine}{await errorTask.ConfigureAwait(false)}");
        }
        finally
        {
            if (!process.HasExited)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
            }
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the state check and cleanup.
        }
    }

    private Process StartInteractiveProcess(IReadOnlyList<string> arguments, string? enginePipeName, string colorRange, AutoCodecDecision codecPolicy, string displayLease)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            }
        };
        process.StartInfo.Environment["PIULINK_QT_HEADLESS"] = "1";
        if (enginePipeName != null) process.StartInfo.Environment["PIULINK_ENGINE_PIPE"] = enginePipeName;
        process.StartInfo.Environment["PIULINK_AUTO_BITRATE"] = "0";
        process.StartInfo.Environment["PIULINK_CODEC_POLICY"] = codecPolicy.PreferAv1 ? "efficiency" : "balanced";
        process.StartInfo.Environment["PIULINK_DISPLAY_LEASE"] = displayLease;
        if (colorRange == "limited") process.StartInfo.Environment["COLOR_RANGE_OVERRIDE"] = "0";
        else if (colorRange == "full") process.StartInfo.Environment["COLOR_RANGE_OVERRIDE"] = "1";
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("PiuLink stream process could not be started.");
        }

        return process;
    }
}

public sealed class MoonlightQtStreamSession : IStreamSession
{
    private readonly Process _process;
    private readonly PiuLinkEngineChannel _channel;
    private readonly Task _completion;

    internal MoonlightQtStreamSession(Process process, PiuLinkEngineChannel channel)
    {
        _process = process;
        _channel = channel;
        _completion = MonitorAsync();
    }

    public PiuLinkEngineChannel Channel => _channel;
    public int ExitCode { get; private set; } = -1;

    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process ended between checking and stopping it.
        }
    }

    public Task WaitForExitAsync(CancellationToken token = default) => _completion.WaitAsync(token);

    public int MousePosition(short x, short y, short width, short height) => 0;
    public int MouseMove(short x, short y) => 0;
    public int MouseButton(int action, int button) => 0;
    public int Scroll(short amount) => 0;
    public int HScroll(short amount) => 0;
    public int Keyboard(short key, int action, byte modifiers) => 0;

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Process cleanup raced with its normal exit.
        }
        _process.Dispose();
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task MonitorAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            ExitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // A startup failure is reported by the caller that created us.
        }
    }
}
