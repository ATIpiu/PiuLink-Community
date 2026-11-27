using System.Text.Json;
using PiuLink.Host.Core.Diagnostics;

namespace PiuLink.Host.Core.Tailscale;

public enum TailscaleConnectState
{
    Missing,
    Connected,
    AwaitingAuthentication,
    NeedsAttention
}

public sealed record TailscaleConnectResult(
    TailscaleConnectState State,
    string Message,
    string? Detail = null,
    Uri? AuthenticationUri = null);

public sealed record TailscalePreferences(
    bool AcceptRoutes,
    bool AcceptDns,
    bool ShieldsUp,
    bool RunSsh,
    bool RunWebClient,
    bool AutoUpdate,
    bool WantRunning);

/// <summary>
/// Starts Tailscale's own supported authentication flow without opening its desktop UI.
/// Tailscale writes its authentication URL to local status instead of opening a browser when
/// CLI output is redirected, so the caller receives the URL and opens it with the system shell.
/// </summary>
public sealed class TailscaleControlService
{
    private static readonly string[] StatusArguments = ["status", "--json", "--peers=false"];

    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _executableLocator;

    public TailscaleControlService(
        IProcessRunner processRunner,
        Func<string?>? executableLocator = null)
    {
        _processRunner = processRunner;
        _executableLocator = executableLocator ?? ExecutableLocator.FindTailscale;
    }

    public static TailscaleControlService CreateDefault() =>
        new(new ProcessRunner());

    public async Task<TailscalePreferences> GetPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var executable = RequireExecutable();
        var result = await _processRunner.RunAsync(
            executable,
            ["debug", "prefs"],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(FirstUsefulLine(result.StandardError, result.StandardOutput) ??
                                                "Tailscale settings could not be read.");
        }

        return ParsePreferences(result.StandardOutput) ??
               throw new InvalidDataException("Tailscale returned invalid settings data.");
    }

    public async Task ApplyPreferencesAsync(
        TailscalePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var executable = RequireExecutable();
        var result = await _processRunner.RunAsync(
            executable,
            [
                "set",
                $"--accept-routes={BooleanArgument(preferences.AcceptRoutes)}",
                $"--accept-dns={BooleanArgument(preferences.AcceptDns)}",
                $"--shields-up={BooleanArgument(preferences.ShieldsUp)}",
                $"--ssh={BooleanArgument(preferences.RunSsh)}",
                $"--webclient={BooleanArgument(preferences.RunWebClient)}",
                $"--auto-update={BooleanArgument(preferences.AutoUpdate)}"
            ],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(FirstUsefulLine(result.StandardError, result.StandardOutput) ??
                                                "Tailscale settings could not be saved.");
        }
    }

    public async Task SetRunningAsync(bool running, CancellationToken cancellationToken = default)
    {
        var executable = RequireExecutable();
        var result = await _processRunner.RunAsync(
            executable,
            running ? ["up", "--timeout=5s"] : ["down"],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        if (!result.Succeeded && !(running && FindSecureAuthenticationUri(
                result.StandardOutput,
                result.StandardError) is not null))
        {
            throw new InvalidOperationException(FirstUsefulLine(result.StandardError, result.StandardOutput) ??
                                                "Tailscale could not change its connection state.");
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var executable = RequireExecutable();
        var result = await _processRunner.RunAsync(
            executable,
            ["logout"],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(FirstUsefulLine(result.StandardError, result.StandardOutput) ??
                                                "Tailscale could not sign out.");
        }
    }

    public async Task<TailscaleConnectResult> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        var executable = _executableLocator();
        if (executable is null)
        {
            return new TailscaleConnectResult(
                TailscaleConnectState.Missing,
                "Install Tailscale before starting sign-in.");
        }

        ProcessResult initialStatus;
        TailscaleStatus? status;
        try
        {
            (initialStatus, status) = await ReadStatusAsync(
                executable,
                attempts: 3,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TailscaleConnectResult(
                TailscaleConnectState.NeedsAttention,
                "PiuLink could not start Tailscale sign-in.",
                exception.Message);
        }

        if (IsRunning(status))
        {
            return new TailscaleConnectResult(
                TailscaleConnectState.Connected,
                "Tailscale is connected.");
        }

        if (status?.AuthenticationUri is not null)
        {
            return AuthenticationRequired(status.AuthenticationUri);
        }

        ProcessResult startResult;
        ProcessResult refreshedStatus;
        try
        {
            // A short, bounded `up` call asks the local service to generate an AuthURL. It must
            // never wait for the user to finish browser authentication inside the UI event.
            startResult = await _processRunner.RunAsync(
                executable,
                ["up", "--timeout=2s"],
                TimeSpan.FromSeconds(5),
                cancellationToken);

            var inlineAuthenticationUri = FindSecureAuthenticationUri(
                startResult.StandardOutput,
                startResult.StandardError);
            if (inlineAuthenticationUri is not null)
            {
                return AuthenticationRequired(inlineAuthenticationUri);
            }

            (refreshedStatus, status) = await ReadStatusAsync(
                executable,
                attempts: 4,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TailscaleConnectResult(
                TailscaleConnectState.NeedsAttention,
                "PiuLink could not start Tailscale sign-in.",
                exception.Message);
        }

        if (IsRunning(status) || startResult.Succeeded)
        {
            return new TailscaleConnectResult(
                TailscaleConnectState.Connected,
                "Tailscale is connected.");
        }

        if (status?.AuthenticationUri is not null)
        {
            return AuthenticationRequired(status.AuthenticationUri);
        }

        return new TailscaleConnectResult(
            TailscaleConnectState.NeedsAttention,
            LooksLikeIncompleteJson(refreshedStatus.StandardOutput)
                ? "Tailscale is still starting. Wait a few seconds, then check again."
                : "Tailscale did not provide a secure sign-in address.",
            FirstUsefulLine(
                refreshedStatus.StandardError,
                refreshedStatus.StandardOutput,
                startResult.StandardError,
                startResult.StandardOutput,
                initialStatus.StandardError));
    }

    private async Task<(ProcessResult Result, TailscaleStatus? Status)> ReadStatusAsync(
        string executable,
        int attempts,
        CancellationToken cancellationToken)
    {
        ProcessResult result = new(-1, string.Empty, string.Empty, false);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            result = await _processRunner.RunAsync(
                executable,
                StatusArguments,
                TimeSpan.FromSeconds(10),
                cancellationToken);
            var status = ParseStatus(result.StandardOutput);
            if (status is not null)
            {
                return (result, status);
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
            }
        }

        return (result, null);
    }

    private static TailscaleConnectResult AuthenticationRequired(Uri uri) => new(
        TailscaleConnectState.AwaitingAuthentication,
        "Your browser is open. Finish signing in, then check the connection again.",
        AuthenticationUri: uri);

    private static bool IsRunning(TailscaleStatus? status) =>
        string.Equals(status?.BackendState, "Running", StringComparison.OrdinalIgnoreCase);

    public static TailscalePreferences? ParsePreferences(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var autoUpdate = root.TryGetProperty("AutoUpdate", out var autoUpdateElement) &&
                             autoUpdateElement.ValueKind == JsonValueKind.Object &&
                             ReadBoolean(autoUpdateElement, "Apply");
            return new TailscalePreferences(
                ReadBoolean(root, "RouteAll"),
                ReadBoolean(root, "CorpDNS"),
                ReadBoolean(root, "ShieldsUp"),
                ReadBoolean(root, "RunSSH"),
                ReadBoolean(root, "RunWebClient"),
                autoUpdate,
                ReadBoolean(root, "WantRunning"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string RequireExecutable() =>
        _executableLocator() ?? throw new FileNotFoundException("Tailscale is not installed.");

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static string BooleanArgument(bool value) => value ? "true" : "false";

    private static Uri? FindSecureAuthenticationUri(params string[] values)
    {
        foreach (var token in values.SelectMany(value => value.Split(
                     [' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var candidateText = token.Trim('"', '\'', '<', '>', '(', ')', '[', ']', ',', ';');
            if (Uri.TryCreate(candidateText, UriKind.Absolute, out var candidate) &&
                candidate.Scheme == Uri.UriSchemeHttps)
            {
                return candidate;
            }
        }

        return null;
    }

    private static TailscaleStatus? ParseStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var backendState = root.TryGetProperty("BackendState", out var backendStateElement)
                ? backendStateElement.GetString()
                : null;
            var authUrl = root.TryGetProperty("AuthURL", out var authUrlElement)
                ? authUrlElement.GetString()
                : null;
            Uri? authenticationUri = null;
            if (Uri.TryCreate(authUrl, UriKind.Absolute, out var candidate) &&
                candidate.Scheme == Uri.UriSchemeHttps)
            {
                authenticationUri = candidate;
            }

            return new TailscaleStatus(backendState, authenticationUri);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeIncompleteJson(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith('{') && !trimmed.EndsWith('}');
    }

    private static string? FirstUsefulLine(params string[] values) => values
        .SelectMany(value => value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(line => line is not "{" and not "}" and not "[" and not "]")
        .FirstOrDefault();

    private sealed record TailscaleStatus(string? BackendState, Uri? AuthenticationUri);
}
