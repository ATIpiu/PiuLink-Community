using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PiuLink.Host.Core.Sunshine;

public sealed record SunshineOverview(
    int ApplicationCount,
    int PairedClientCount,
    int CurrentApplicationId,
    int ActiveStreamCount)
{
    public bool HasCurrentApplication => CurrentApplicationId != 0;
    public bool HasActiveStreams => ActiveStreamCount != 0; // Unknown fails closed.
    public bool HasRetainedApplication => HasCurrentApplication && !HasActiveStreams;
}

public sealed record SunshineDisplayPreparation(SunshineDisplayState State, int ActiveStreams,
    int CurrentApplicationId, string Configuration)
{
    public bool SupportsSessionOutput { get; init; }

    public bool CanCaptureExtendedOutput(SunshineDisplay? target, bool exclusive) =>
        SupportsSessionOutput && !exclusive && ActiveStreams == 0 && CurrentApplicationId >= 0 &&
        target is { IsVirtual: false, Width: > 0, Height: > 0, Bounds: not null } &&
        Guid.TryParse(target.DeviceId, out _) && State.Displays.Contains(target);

    // A prepared launch skips the engine's desktop reconfiguration. With exactly
    // one attached output, its Windows capture enumerator has only one possible source.
    public bool CanUseSolePhysicalOutput(SunshineDisplay? target, int windowsPathCount) =>
        ActiveStreams == 0 && CurrentApplicationId >= 0 && target is { IsVirtual: false, Width: > 0, Height: > 0 } && windowsPathCount == 1 &&
        State.Displays.Count(d => d.Width > 0 && d.Height > 0) == 1 &&
        State.Displays.Any(d => d == target);
}

public sealed record DesktopRectangle(int X, int Y, int Width, int Height);

public sealed record SunshineDisplay(
    string DeviceId,
    string DisplayName,
    string FriendlyName,
    bool IsPrimary,
    bool IsVirtual,
    int? Width,
    int? Height,
    double? RefreshRate)
{
    public DesktopRectangle? Bounds { get; init; }
    public string ResolutionText => Width.HasValue && Height.HasValue
        ? $"{Width} × {Height}"
        : "Inactive";
}

public sealed record SunshineDisplayState(
    IReadOnlyList<SunshineDisplay> Displays,
    string? SelectedDeviceId,
    bool ExclusiveDisplay,
    bool SmartDisplay,
    bool HdrAuto = false);

/// <summary>
/// Small native client for Sunshine's loopback REST API. Credentials are supplied per request
/// so callers can keep them in memory only instead of writing them to PiuLink configuration.
/// </summary>
public sealed class SunshineApiClient : IDisposable
{
    public static readonly Uri DefaultEndpoint = new("https://localhost:47990/");
    private static readonly Regex ActiveSessionsPattern = new(
        @"active sessions:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _virtualAppRegistration = new(1, 1);

    public SunshineApiClient(HttpClient httpClient, Uri? endpoint = null)
        : this(httpClient, endpoint ?? DefaultEndpoint, ownsHttpClient: false)
    {
    }

    private SunshineApiClient(HttpClient httpClient, Uri endpoint, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _ownsHttpClient = ownsHttpClient;
    }

    public static SunshineApiClient CreateForLoopback()
    {
        var handler = new HttpClientHandler
        {
            // Requests to Sunshine never leave this PC. Explicitly bypass system and environment
            // proxies so a user's HTTP_PROXY/HTTPS_PROXY settings cannot intercept localhost.
            UseProxy = false,
            // Sunshine uses a local self-signed certificate by default. The exception is deliberately
            // restricted to a loopback request; certificates for every non-local endpoint remain rejected.
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri?.IsLoopback == true
        };
        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        return new SunshineApiClient(httpClient, DefaultEndpoint, ownsHttpClient: true);
    }

    public async Task<SunshineOverview> GetOverviewAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);

        var appsTask = GetTextAsync(new Uri(_endpoint, "api/apps"),
            username, password, cancellationToken);
        var clientsTask = GetTextAsync(new Uri(_endpoint, "api/clients/list"),
            username, password, cancellationToken);
        var stateTask = GetJsonAsync("api/piulink/state", username, password, cancellationToken);
        await Task.WhenAll(appsTask, clientsTask, stateTask);
        using var apps = JsonDocument.Parse(await appsTask);
        using var clients = JsonDocument.Parse(await clientsTask);
        using var state = await stateTask;
        var currentApplicationId = state.RootElement.GetProperty("currentApplicationId").GetInt32();
        return new SunshineOverview(
            CountArray(apps.RootElement, "apps", "applications"),
            CountArray(clients.RootElement, "named_certs", "clients", "paired_clients"),
            currentApplicationId,
            state.RootElement.GetProperty("activeStreams").GetInt32());
    }

    public async Task<int> GetActiveStreamCountAsync(string username, string password, CancellationToken token)
    {
        using var state = await GetJsonAsync("api/piulink/state", username, password, token);
        return state.RootElement.GetProperty("activeStreams").GetInt32();
    }

    public async Task<(int Streams, bool LiveVideoResize, bool LiveVideoFrameRate)> GetDisplaySessionStatusAsync(string username, string password, CancellationToken token)
    {
        using var state = await GetJsonAsync("api/piulink/state", username, password, token);
        var root = state.RootElement;
        return (root.GetProperty("activeStreams").GetInt32(), root.TryGetProperty("liveVideoResize", out var value) && value.ValueKind == JsonValueKind.True,
            root.TryGetProperty("liveVideoFrameRate", out var rate) && rate.ValueKind == JsonValueKind.True);
    }

    public sealed record VideoSize(int Width, int Height, int Fps = 0);

    public async Task<VideoSize> LiveVideoModeAsync(string sessionId, string owner, string username, string password,
        VideoSize? desired, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/piulink/prepared", username, password);
        request.Content = JsonContent.Create(new { requestId = sessionId, owner, action = desired == null ? "video-size" : "resize-video",
            width = desired?.Width ?? 0, height = desired?.Height ?? 0, fps = desired?.Fps ?? 0 });
        using var response = await _httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var actual = new VideoSize(json.RootElement.GetProperty("width").GetInt32(), json.RootElement.GetProperty("height").GetInt32(),
            json.RootElement.TryGetProperty("fps", out var rate) ? rate.GetInt32() : 0);
        if (actual.Width <= 0 || actual.Height <= 0 || (desired != null && (actual.Width != desired.Width || actual.Height != desired.Height ||
            (desired.Fps > 0 && actual.Fps != desired.Fps))))
            throw new InvalidDataException("Encoder did not confirm the requested video dimensions.");
        return actual;
    }

    public async Task CloseCurrentApplicationAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);

        using var request = CreateRequest(HttpMethod.Post, "api/apps/close", username, password);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return;
        }

        using var result = JsonDocument.Parse(responseBody);
        if (result.RootElement.ValueKind == JsonValueKind.Object &&
            result.RootElement.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException("Sunshine did not close the current application.");
        }
    }

    public async Task RegisterPreparedDisplayLeaseAsync(string requestId, string owner, string username, string password, CancellationToken token,
        bool ready = true, string mode = "", string action = "", string? output = null)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/piulink/prepared", username, password);
        request.Content = JsonContent.Create(new { requestId, owner, ready, mode, action, output = output ?? "" });
        using var response = await _httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        if (output != null) {
            using var ack = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!ack.RootElement.TryGetProperty("output", out var accepted) || accepted.GetString() != output)
                throw new InvalidDataException("The streaming engine did not confirm the selected capture output.");
        }
    }

    public async Task RemoveManagedVirtualApplicationsAsync(string username, string password,
        CancellationToken cancellationToken = default)
    {
        await _virtualAppRegistration.WaitAsync(cancellationToken);
        try {
            for (int attempt = 0; attempt < 32; attempt++) {
                using var inventory = await GetJsonAsync("api/apps", username, password, cancellationToken);
                var apps = inventory.RootElement.GetProperty("apps").EnumerateArray().ToArray();
                var index = Array.FindIndex(apps, app => app.TryGetProperty("piulink-managed-app", out var marker) &&
                    marker.ValueKind == JsonValueKind.String && marker.GetString() is "terminal" or "codex");
                if (index < 0) return;
                using var request = CreateRequest(HttpMethod.Delete, "api/apps/" + index, username, password);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (!result.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("The engine did not remove the retired virtual application.");
            }
            throw new InvalidOperationException("Virtual application cleanup did not converge; refresh and retry.");
        } finally { _virtualAppRegistration.Release(); }
    }

    public async Task SubmitPinAsync(
        string pin,
        string clientName,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        if (!IsValidPairingPin(pin))
        {
            throw new ArgumentException("The Moonlight pairing PIN must contain exactly four digits.", nameof(pin));
        }

        if (string.IsNullOrWhiteSpace(clientName))
        {
            throw new ArgumentException("A client name is required.", nameof(clientName));
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(HttpMethod.Post, "api/pin", username, password);
            // The Sunshine service may have restarted since this client was
            // created. Never submit a PIN over a pooled pre-restart connection.
            request.Headers.ConnectionClose = true;
            request.Content = JsonContent.Create(new { pin, name = clientName.Trim() });
            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException exception) when (exception.StatusCode is null &&
                attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
    }

    public async Task<SunshineDisplayState> GetDisplayStateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        var configurationTask = GetConfigurationAsync(username, password, cancellationToken);
        var stateTask = GetJsonAsync("api/piulink/state", username, password, cancellationToken);
        await Task.WhenAll(configurationTask, stateTask);
        var configuration = await configurationTask;
        using var liveState = await stateTask;

        return ParseDisplayState(configuration, liveState.RootElement);
    }

    public async Task<SunshineDisplayPreparation> GetDisplayPreparationAsync(
        string username, string password, CancellationToken cancellationToken)
    {
        ValidateCredentials(username, password);
        var configurationTask = GetConfigurationAsync(username, password, cancellationToken);
        var liveTask = GetJsonAsync("api/piulink/state", username, password, cancellationToken);
        await Task.WhenAll(configurationTask, liveTask);
        using var live = await liveTask;
        var configuration = await configurationTask;
        RemoveResponseMetadata(configuration);
        var root = live.RootElement;
        return new(ParseDisplayState(configuration, root), root.GetProperty("activeStreams").GetInt32(),
            root.GetProperty("currentApplicationId").GetInt32(), JsonSerializer.Serialize(configuration)) {
            SupportsSessionOutput = root.TryGetProperty("perSessionCaptureOutput", out var capability) &&
                capability.ValueKind == JsonValueKind.True
        };
    }

    private static SunshineDisplayState ParseDisplayState(IReadOnlyDictionary<string, JsonElement> configuration,
        JsonElement liveState)
    {
        var selectedDeviceId = GetConfigurationString(configuration, "output_name");
        var exclusiveDisplay = string.Equals(
            GetConfigurationString(configuration, "dd_configuration_option"),
            "ensure_only_display",
            StringComparison.OrdinalIgnoreCase);
        var smartDisplay = !string.IsNullOrWhiteSpace(selectedDeviceId) &&
                           IsConfigurationValue(configuration, "dd_resolution_option", "auto") &&
                           IsConfigurationValue(configuration, "dd_refresh_rate_option", "auto");
        return new SunshineDisplayState(
            ParseDisplayArray(liveState.GetProperty("displays").GetRawText(), selectedDeviceId),
            selectedDeviceId,
            exclusiveDisplay,
            smartDisplay,
            IsConfigurationValue(configuration, "dd_hdr_option", "auto"));
    }

    public async Task<string> GetConfigurationJsonAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        var configuration = await GetConfigurationAsync(username, password, cancellationToken);
        RemoveResponseMetadata(configuration);
        return JsonSerializer.Serialize(configuration, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public async Task ApplyConfigurationJsonAsync(
        string configurationJson,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            throw new ArgumentException("Sunshine configuration JSON is required.", nameof(configurationJson));
        }

        Dictionary<string, JsonElement> configuration;
        try
        {
            using var document = JsonDocument.Parse(configurationJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Sunshine configuration must be a JSON object.", nameof(configurationJson));
            }

            configuration = document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"Sunshine configuration JSON is invalid: {exception.Message}",
                nameof(configurationJson),
                exception);
        }

        RemoveResponseMetadata(configuration);
        using (var request = CreateRequest(HttpMethod.Post, "api/config", username, password))
        {
            request.Content = JsonContent.Create(configuration);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await RestartAsync(username, password, cancellationToken);
    }

    public async Task ApplyIdleConfigurationJsonAsync(string json, string username, string password,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString();
        var owner = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        // The engine takes its launch/resume mutex and checks the live stream count when issuing this lease.
        await RegisterPreparedDisplayLeaseAsync(id, owner, username, password, cancellationToken, ready: false);
        bool primaryFailed = false;
        try
        {
            var state = await GetOverviewAsync(username, password, cancellationToken);
            if (state.HasActiveStreams || state.HasCurrentApplication)
                throw new InvalidOperationException("请先结束当前串流和保留的应用，再保存高级设置。");
            await ApplyConfigurationJsonAsync(json, username, password, cancellationToken);
        }
        catch
        {
            primaryFailed = true;
            throw;
        }
        finally
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await RegisterPreparedDisplayLeaseAsync(id, owner, username, password, attemptTimeout.Token, action: "abort");
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    if (attempt < 12)
                    {
                        await Task.Delay(400);
                        continue;
                    }
                    if (!primaryFailed) throw;
                    break;
                }
            }
        }
    }

    // Restore only the fields changed by ApplyDisplaySettingsAsync. Keep unrelated edits made during the session.
    public async Task<bool> RestoreDisplayConfigurationAsync(string originalJson, string username, string password,
        bool allowRestart)
    {
        var current = await GetConfigurationAsync(username, password, CancellationToken.None);
        RemoveResponseMetadata(current);
        using var original = JsonDocument.Parse(originalJson);
        bool changed = false;
        foreach (var name in new[] { "output_name", "dd_configuration_option", "dd_resolution_option",
                     "dd_refresh_rate_option", "dd_hdr_option", "dd_config_revert_on_disconnect" })
        {
            if (original.RootElement.TryGetProperty(name, out var value))
            {
                if (current.TryGetValue(name, out var now) && now.GetRawText() == value.GetRawText()) continue;
                current[name] = value.Clone();
                changed = true;
            }
            else changed |= current.Remove(name);
        }
        if (!changed) return true;
        if (!allowRestart) return false;
        await ApplyConfigurationJsonAsync(JsonSerializer.Serialize(current), username, password);
        return true;
    }

    public async Task ApplyDisplaySettingsAsync(
        string? deviceId,
        bool exclusiveDisplay,
        bool smartDisplay,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        if (!string.IsNullOrWhiteSpace(deviceId) && !Guid.TryParse(deviceId, out _))
        {
            throw new ArgumentException("The Sunshine display identifier is invalid.", nameof(deviceId));
        }

        var configuration = await GetConfigurationAsync(username, password, cancellationToken);
        RemoveResponseMetadata(configuration);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            configuration.Remove("output_name");
            configuration.Remove("dd_configuration_option");
            configuration.Remove("dd_resolution_option");
            configuration.Remove("dd_refresh_rate_option");
            configuration.Remove("dd_hdr_option");
            configuration.Remove("dd_config_revert_on_disconnect");
        }
        else
        {
            configuration["output_name"] = JsonSerializer.SerializeToElement(deviceId);
            configuration["dd_configuration_option"] = JsonSerializer.SerializeToElement(
                exclusiveDisplay ? "ensure_only_display" : "ensure_active");
            var displayMode = smartDisplay ? "auto" : "disabled";
            configuration["dd_resolution_option"] = JsonSerializer.SerializeToElement(displayMode);
            configuration["dd_refresh_rate_option"] = JsonSerializer.SerializeToElement(displayMode);
            // HDR follows the client's request even when resolution and refresh rate
            // are fixed. Otherwise selecting "stream default" silently forces SDR.
            configuration["dd_hdr_option"] = JsonSerializer.SerializeToElement("auto");
            // Sunshine otherwise keeps the altered topology while an application remains active.
            // PiuLink promises to restore the user's desktop after the last client disconnects.
            configuration["dd_config_revert_on_disconnect"] = JsonSerializer.SerializeToElement("true");
        }

        using (var request = CreateRequest(HttpMethod.Post, "api/config", username, password))
        {
            request.Content = JsonContent.Create(configuration);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await RestartAsync(username, password, cancellationToken);

        await VerifyDisplaySettingsAfterRestartAsync(
            deviceId,
            exclusiveDisplay,
            smartDisplay,
            username,
            password,
            cancellationToken);
    }

    public async Task ApplyTrustedPairingSettingsAsync(
        bool autoPairLan,
        bool autoPairTailnet,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        var configuration = await GetConfigurationAsync(username, password, cancellationToken);
        RemoveResponseMetadata(configuration);
        configuration["piulink_auto_pair_lan"] = JsonSerializer.SerializeToElement(autoPairLan);
        configuration["piulink_auto_pair_tailnet"] = JsonSerializer.SerializeToElement(autoPairTailnet);

        using (var request = CreateRequest(HttpMethod.Post, "api/config", username, password))
        {
            request.Content = JsonContent.Create(configuration);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await RestartAsync(username, password, cancellationToken);

        await VerifyTrustedPairingSettingsAfterRestartAsync(
            autoPairLan,
            autoPairTailnet,
            username,
            password,
            cancellationToken);
    }

    public async Task RestartAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "api/restart", username, password);
            request.Content = JsonContent.Create(new { });
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException exception) when (IsExpectedRestartDisconnect(exception))
        {
            // Sunshine commonly closes its loopback socket while handling /api/restart.
            // Callers verify that the service returns before continuing.
        }
    }

    public static IReadOnlyList<SunshineDisplay> ParseDisplays(string logs, string? selectedDeviceId = null)
    {
        var markers = new[]
        {
            "Currently available display devices:",
            "Currently available devices:"
        };
        var latestMarker = markers
            .Select(marker => new
            {
                Text = marker,
                Index = logs.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase)
            })
            .OrderByDescending(candidate => candidate.Index)
            .First();
        var markerIndex = latestMarker.Index;
        if (markerIndex < 0)
        {
            return [];
        }

        var arrayStart = logs.IndexOf('[', markerIndex + latestMarker.Text.Length);
        if (arrayStart < 0 || !TryFindJsonArrayEnd(logs, arrayStart, out var arrayEnd))
        {
            return [];
        }

        return ParseDisplayArray(logs[arrayStart..(arrayEnd + 1)], selectedDeviceId);
    }

    public static IReadOnlyList<SunshineDisplay> ParseDisplayArray(string json, string? selectedDeviceId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var displays = new List<SunshineDisplay>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var deviceId = GetString(item, "device_id");
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    continue;
                }

                var displayName = GetString(item, "display_name");
                if (string.IsNullOrWhiteSpace(displayName)) displayName = "Unattached display";
                var friendlyName = GetString(item, "friendly_name");
                if (string.IsNullOrWhiteSpace(friendlyName)) friendlyName = displayName;
                var manufacturer = item.TryGetProperty("edid", out var edid) && edid.ValueKind == JsonValueKind.Object
                    ? GetString(edid, "manufacturer_id")
                    : null;
                var info = item.TryGetProperty("info", out var infoValue) &&
                           infoValue.ValueKind == JsonValueKind.Object
                    ? infoValue
                    : default;
                int? width = null;
                int? height = null;
                double? refreshRate = null;
                var isPrimary = false;
                if (info.ValueKind == JsonValueKind.Object)
                {
                    isPrimary = info.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True;
                    if (info.TryGetProperty("resolution", out var resolution) && resolution.ValueKind == JsonValueKind.Object)
                    {
                        width = GetInt32(resolution, "width");
                        height = GetInt32(resolution, "height");
                    }

                    if (info.TryGetProperty("refresh_rate", out var refresh) &&
                        refresh.ValueKind == JsonValueKind.Object &&
                        refresh.TryGetProperty("value", out var rational) &&
                        rational.ValueKind == JsonValueKind.Object)
                    {
                        var numerator = GetInt32(rational, "numerator");
                        var denominator = GetInt32(rational, "denominator");
                        if (numerator.HasValue && denominator is > 0)
                        {
                            refreshRate = numerator.Value / (double)denominator.Value;
                        }
                    }
                }

                var isVirtual = friendlyName.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                                friendlyName.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
                                friendlyName.Contains("IDD", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(manufacturer, "MTT", StringComparison.OrdinalIgnoreCase);
                displays.Add(new SunshineDisplay(
                    deviceId,
                    displayName,
                    friendlyName,
                    isPrimary,
                    isVirtual,
                    width,
                    height,
                    refreshRate) {
                    Bounds = info.ValueKind == JsonValueKind.Object &&
                        info.TryGetProperty("origin_point", out var origin) && origin.ValueKind == JsonValueKind.Object &&
                        GetInt32(origin, "x") is int x && GetInt32(origin, "y") is int y &&
                        width is > 0 && height is > 0 ? new(x, y, width.Value, height.Value) : null
                });
            }

            // Sunshine can log the same monitor more than once while Windows is changing
            // topology. Virtual display drivers can also leave several unattached children
            // with distinct device IDs but the same logical display. Keep one useful row for
            // each real target instead of exposing every transient log record in the UI.
            var uniqueDeviceIds = displays
                .GroupBy(display => display.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => SelectBestDisplay(group, selectedDeviceId));
            return uniqueDeviceIds.ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string GetLogicalDisplayKey(SunshineDisplay display)
    {
        if (IsAttachedDisplay(display))
        {
            return "path:" + display.DisplayName.Trim();
        }

        if (display.IsVirtual && !string.IsNullOrWhiteSpace(display.FriendlyName))
        {
            return "virtual:" + display.FriendlyName.Trim();
        }

        return "device:" + display.DeviceId;
    }

    private static SunshineDisplay SelectBestDisplay(
        IEnumerable<SunshineDisplay> candidates,
        string? selectedDeviceId) => candidates
        .OrderByDescending(IsAttachedDisplay)
        .ThenByDescending(display => display.Width.HasValue && display.Height.HasValue)
        .ThenByDescending(display => !string.IsNullOrWhiteSpace(selectedDeviceId) &&
                                     string.Equals(display.DeviceId, selectedDeviceId, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(display => display.IsPrimary)
        .First();

    private static bool IsAttachedDisplay(SunshineDisplay display) =>
        !string.IsNullOrWhiteSpace(display.DisplayName) &&
        !string.Equals(display.DisplayName, "Unattached display", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidPairingPin(string? pin) =>
        pin is { Length: 4 } && pin.All(character => character is >= '0' and <= '9');

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        string relativePath,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        return JsonDocument.Parse(await GetTextAsync(
            new Uri(_endpoint, relativePath), username, password, cancellationToken));
    }

    private async Task<Dictionary<string, JsonElement>> GetConfigurationAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/config", username, password, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Sunshine returned an invalid configuration payload.");
        }

        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task VerifyDisplaySettingsAfterRestartAsync(
        string? deviceId,
        bool exclusiveDisplay,
        bool smartDisplay,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        var delays = new[]
        {
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(350),
            TimeSpan.FromMilliseconds(600),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(2)
        };

        foreach (var delay in delays)
        {
            await Task.Delay(delay, cancellationToken);
            try
            {
                var configuration = await GetConfigurationAsync(username, password, cancellationToken);
                if (DisplaySettingsMatch(configuration, deviceId, exclusiveDisplay, smartDisplay))
                {
                    return;
                }

                lastFailure = new InvalidDataException(
                    "Sunshine restarted but did not preserve the selected display settings.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                lastFailure = exception;
            }
        }

        throw new InvalidOperationException(
            "Sunshine did not return with the selected display settings after restart.",
            lastFailure);
    }

    private async Task VerifyTrustedPairingSettingsAfterRestartAsync(
        bool autoPairLan,
        bool autoPairTailnet,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        var delays = new[]
        {
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(350),
            TimeSpan.FromMilliseconds(600),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(2)
        };

        foreach (var delay in delays)
        {
            await Task.Delay(delay, cancellationToken);
            try
            {
                var configuration = await GetConfigurationAsync(username, password, cancellationToken);
                if (GetConfigurationBoolean(configuration, "piulink_auto_pair_lan") == autoPairLan &&
                    GetConfigurationBoolean(configuration, "piulink_auto_pair_tailnet") == autoPairTailnet)
                {
                    return;
                }

                lastFailure = new InvalidDataException(
                    "Sunshine restarted but did not preserve the trusted pairing settings.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                lastFailure = exception;
            }
        }

        throw new InvalidOperationException(
            "Sunshine did not return with the trusted pairing settings after restart.",
            lastFailure);
    }

    private static bool GetConfigurationBoolean(
        IReadOnlyDictionary<string, JsonElement> configuration,
        string name)
    {
        if (!configuration.TryGetValue(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    private static bool DisplaySettingsMatch(
        IReadOnlyDictionary<string, JsonElement> configuration,
        string? deviceId,
        bool exclusiveDisplay,
        bool smartDisplay)
    {
        var configuredDevice = GetConfigurationString(configuration, "output_name");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.IsNullOrWhiteSpace(configuredDevice);
        }

        var expectedMode = exclusiveDisplay ? "ensure_only_display" : "ensure_active";
        var expectedDisplayMode = smartDisplay ? "auto" : "disabled";
        return string.Equals(configuredDevice, deviceId, StringComparison.OrdinalIgnoreCase) &&
               IsConfigurationValue(configuration, "dd_configuration_option", expectedMode) &&
               IsConfigurationValue(configuration, "dd_resolution_option", expectedDisplayMode) &&
               IsConfigurationValue(configuration, "dd_refresh_rate_option", expectedDisplayMode) &&
               IsConfigurationValue(configuration, "dd_hdr_option", "auto") &&
               IsConfigurationValue(configuration, "dd_config_revert_on_disconnect", "true");
    }

    private static bool IsExpectedRestartDisconnect(HttpRequestException exception) =>
        exception.InnerException is HttpIOException
        {
            HttpRequestError: HttpRequestError.ResponseEnded
        };

    private async Task<string> GetTextAsync(
        Uri uri,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(HttpMethod.Get, uri, username, password);
            // Only replay read-only requests. Never retry a close, configuration write or restart.
            if (attempt > 0) request.Headers.ConnectionClose = true;
            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is null &&
                attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(150, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new HttpRequestException($"Sunshine GET {uri.AbsolutePath} failed.",
                    exception, exception.StatusCode);
            }
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        string username,
        string password) =>
        CreateRequest(method, new Uri(_endpoint, relativePath), username, password);

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        string username,
        string password)
    {
        var request = new HttpRequestMessage(method, uri);
        var credentialBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(credentialBytes));
        return request;
    }

    private Uri BuildServerInfoUri()
    {
        var builder = new UriBuilder(_endpoint)
        {
            Scheme = Uri.UriSchemeHttp,
            Port = _endpoint.Port - 1,
            Path = "serverinfo",
            Query = "uniqueid=piulink-host"
        };
        return builder.Uri;
    }

    private static int ParseCurrentApplicationId(string serverInfo)
    {
        var document = XDocument.Parse(serverInfo, LoadOptions.None);
        return int.TryParse(document.Root?.Element("currentgame")?.Value, out var appId)
            ? appId
            : 0;
    }

    private static int ParseActiveStreamCount(string logs)
    {
        var activeStreams = 0;
        foreach (var line in logs.Split('\n'))
        {
            var match = ActiveSessionsPattern.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var reportedCount))
            {
                activeStreams = Math.Max(0, reportedCount);
            }
            else if (line.Contains("CLIENT CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                // This fallback covers a truncated log whose corresponding session-start line is gone.
                activeStreams = Math.Max(1, activeStreams);
            }
            else if (line.Contains("CLIENT DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                activeStreams = Math.Max(0, activeStreams - 1);
            }
        }

        return activeStreams;
    }

    private static int CountArray(JsonElement root, params string[] propertyNames)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.GetArrayLength();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (propertyNames.Any(name =>
                    string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value.GetArrayLength();
            }
        }

        return 0;
    }

    private static string? GetConfigurationString(
        IReadOnlyDictionary<string, JsonElement> configuration,
        string key) =>
        configuration.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsConfigurationValue(
        IReadOnlyDictionary<string, JsonElement> configuration,
        string key,
        string expected) =>
        string.Equals(
            GetConfigurationString(configuration, key),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static void RemoveResponseMetadata(IDictionary<string, JsonElement> configuration)
    {
        configuration.Remove("status");
        configuration.Remove("platform");
        configuration.Remove("version");
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static bool TryFindJsonArrayEnd(string text, int arrayStart, out int arrayEnd)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = arrayStart; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '[')
            {
                depth++;
            }
            else if (character == ']' && --depth == 0)
            {
                arrayEnd = index;
                return true;
            }
        }

        arrayEnd = -1;
        return false;
    }

    private static void ValidateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("A Sunshine administrator username is required.", nameof(username));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("A Sunshine administrator password is required.", nameof(password));
        }
    }
}
