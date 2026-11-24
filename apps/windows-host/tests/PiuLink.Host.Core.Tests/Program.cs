using System.Net;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiuLink.Host.Core.Diagnostics;
using PiuLink.Host.Core.Models;
using PiuLink.Host.Core.Install;
using PiuLink.Host.Core.Sunshine;
using PiuLink.Host.Core.Tailscale;
using PiuLink.Host.Core.Tests;

var tests = new (string Name, Func<Task> Run)[]
{
    ("GPU vendors are classified", TestGpuClassification),
    ("Running Tailscale is ready", TestTailscaleReady),
    ("Tailscale status with empty TailscaleIPs array does not crash", TestTailscaleEmptyIps),
    ("Missing Tailscale is reported", TestTailscaleMissing),
    ("Host readiness accepts a healthy LAN or Tailscale route", TestHostReadiness),
    ("Firewall rule JSON supports one or many rules", TestFirewallRuleParsing),
    ("Artifact hash verification rejects mismatches", TestArtifactHashVerification),
    ("Sunshine overview uses local basic authentication", TestSunshineOverview),
    ("Sunshine read retries are bounded and preserve HTTP errors", TestSunshineReadFailures),
    ("Sunshine closes a retained application through the local API", TestSunshineCloseCurrentApplication),
    ("Sunshine pairing sends the Moonlight PIN", TestSunshinePairing),
    ("Sunshine pairing PIN validation is strict", TestSunshinePinValidation),
    ("Sunshine display inventory parses physical and virtual displays", TestSunshineDisplayParsing),
    ("Sunshine display inventory uses the newest topology", TestSunshineLatestDisplayParsing),
    ("Sunshine display inventory removes transient duplicates", TestSunshineDuplicateDisplayParsing),
    ("Sunshine display routing preserves config and restarts safely", TestSunshineDisplayRouting),
    ("Sunshine complete configuration strips response metadata and restarts", TestSunshineCompleteConfiguration),
    ("Advanced settings reserve the engine and preserve retained applications", TestIdleConfiguration),
    ("Display recovery preserves unrelated config and defers a disruptive restart", TestRestoreConfiguration),
    ("Sunshine restart socket closure is treated as expected", TestSunshineRestartDisconnect),
    ("Session Guardian waits before recovering a retained app", TestSessionGuardianGracePeriod),
    ("Session Guardian resets when an active client returns", TestSessionGuardianActiveReset),
    ("Guided Tailscale sign-in returns the secure AuthURL", TestTailscaleGuidedSignIn),
    ("Guided Tailscale sign-in generates a missing AuthURL without blocking", TestTailscaleAuthUrlGeneration),
    ("Guided Tailscale sign-in retries incomplete status JSON", TestTailscaleIncompleteStatusRetry),
    ("Guided Tailscale sign-in accepts the secure URL printed by Tailscale up", TestTailscaleInlineAuthUrl),
    ("Tailscale preferences parse and save through supported set flags", TestTailscalePreferences),
    ("Bundled Sunshine manifest and payload reject tampering", TestBundledSunshinePayload),
    ("Wake-on-LAN device information is normalized", TestWakeOnLanUtilities)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static Task TestWakeOnLanUtilities()
{
    WakeOnLanServiceTests.Run();
    return Task.CompletedTask;
}

static Task TestGpuClassification()
{
    AssertEqual(GpuVendor.Nvidia, GpuProbe.ClassifyVendor("NVIDIA GeForce RTX 4090"));
    AssertEqual(GpuVendor.Amd, GpuProbe.ClassifyVendor("AMD Radeon RX 7900 XTX"));
    AssertEqual(GpuVendor.Intel, GpuProbe.ClassifyVendor("Intel Arc A770"));
    AssertEqual(GpuVendor.Unknown, GpuProbe.ClassifyVendor("Virtual Display Adapter"));
    return Task.CompletedTask;
}

static async Task TestTailscaleReady()
{
    const string json = "{\"BackendState\":\"Running\",\"TailscaleIPs\":[\"100.64.0.10\"],\"Self\":{\"HostName\":\"studio-pc\"}}";
    var probe = new TailscaleProbe(new FakeRunner(new ProcessResult(0, json, string.Empty, false)), () => "tailscale.exe");
    var result = await probe.CheckAsync();

    AssertEqual(ComponentState.Ready, result.Health.State);
    AssertEqual("100.64.0.10", result.IpAddress);
    AssertEqual("studio-pc", result.DeviceName);
}

static async Task TestTailscaleEmptyIps()
{
    const string json = "{\"BackendState\":\"NeedsLogin\",\"TailscaleIPs\":[],\"Self\":{\"HostName\":\"studio-pc\"}}";
    var probe = new TailscaleProbe(new FakeRunner(new ProcessResult(0, json, string.Empty, false)), () => "tailscale.exe");
    var result = await probe.CheckAsync();

    AssertEqual(ComponentState.NeedsAttention, result.Health.State);
    AssertEqual(null, result.IpAddress);
    AssertEqual("studio-pc", result.DeviceName);
}

static async Task TestTailscaleMissing()
{
    var probe = new TailscaleProbe(new FakeRunner(new ProcessResult(0, "{}", string.Empty, false)), () => null);
    var result = await probe.CheckAsync();
    AssertEqual(ComponentState.Missing, result.Health.State);
}

static Task TestHostReadiness()
{
    var ready = new ComponentHealth("component", ComponentState.Ready, "ready");
    var snapshot = new HostSnapshot(
        DateTimeOffset.UtcNow,
        "pc",
        "Windows",
        Architecture.X64,
        [],
        ready,
        ready,
        "100.64.0.10");
    AssertEqual(true, snapshot.IsReady);
    AssertEqual(false, (snapshot with { TailscaleIp = null }).IsReady);
    var lan = snapshot with { TailscaleIp = null, Tailscale = ready with { State = ComponentState.Missing },
        LanAddresses = ["192.0.2.5"] };
    AssertEqual(true, lan.IsReady);
    AssertEqual(true, lan.ConnectionSummary.Contains("192.0.2.5"));
    AssertEqual(false, (lan with { Sunshine = ready with { State = ComponentState.Missing } }).IsReady);
    return Task.CompletedTask;
}

static Task TestFirewallRuleParsing()
{
    AssertEqual(1, FirewallProbe.ReadRuleNames("\"Sunshine TCP\"").Count);
    AssertEqual(2, FirewallProbe.ReadRuleNames("[\"Sunshine TCP\",\"Sunshine UDP\"]").Count);
    AssertEqual(0, FirewallProbe.ReadRuleNames(string.Empty).Count);
    AssertEqual(0, FirewallProbe.ReadRuleNames("not-json").Count);
    return Task.CompletedTask;
}

static async Task TestArtifactHashVerification()
{
    var path = Path.Combine(Path.GetTempPath(), $"piulink-hash-test-{Guid.NewGuid():N}.txt");
    try
    {
        await File.WriteAllTextAsync(path, "PiuLink");
        AssertEqual(true, await ArtifactHashVerifier.VerifyAsync(
            path,
            "86E736308586C8C000BD32FE4ADAB4A55406D95A58A80863AE29D9652C93AD1F"));
        AssertEqual(false, await ArtifactHashVerifier.VerifyAsync(path, new string('0', 64)));
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task TestSunshineOverview()
{
    var requestCount = 0;
    using var httpClient = new HttpClient(new FakeHttpHandler((request, _) =>
    {
        var requestNumber = Interlocked.Increment(ref requestCount);
        if (requestNumber == 1) throw new HttpRequestException("Simulated connection reset");
        AssertEqual("Basic", request.Headers.Authorization?.Scheme);
        AssertEqual(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret")),
            request.Headers.Authorization?.Parameter);

        var payload = request.RequestUri?.AbsolutePath switch
        {
            "/api/apps" => "{\"apps\":[{\"name\":\"Desktop\"},{\"name\":\"Steam\"}]}",
            "/api/clients/list" => "{\"named_certs\":[{\"name\":\"Tablet\"}]}",
            "/serverinfo" => "<root status_code=\"200\"><currentgame>881448767</currentgame><state>SUNSHINE_SERVER_BUSY</state></root>",
            "/api/piulink/state" => "{\"currentApplicationId\":881448767,\"activeStreams\":0,\"displays\":[]}",
            _ => throw new InvalidOperationException("Unexpected Sunshine API path.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/plain")
        });
    }));
    using var client = new SunshineApiClient(httpClient, new Uri("https://localhost:47990/"));

    var overview = await client.GetOverviewAsync("admin", "secret");

    AssertEqual(4, requestCount);
    AssertEqual(2, overview.ApplicationCount);
    AssertEqual(1, overview.PairedClientCount);
    AssertEqual(881448767, overview.CurrentApplicationId);
    AssertEqual(0, overview.ActiveStreamCount);
    AssertEqual(true, overview.HasRetainedApplication);
}

static async Task TestSunshineReadFailures()
{
    foreach (var status in new HttpStatusCode?[] { null, HttpStatusCode.Unauthorized })
    {
        var requests = 0;
        using var http = new HttpClient(new FakeHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new HttpRequestException("Simulated read failure", null, status);
        }));
        using var client = new SunshineApiClient(http);
        try
        {
            await client.GetOverviewAsync("admin", "secret");
            throw new InvalidOperationException("Read failure was swallowed");
        }
        catch (HttpRequestException error)
        {
            AssertEqual(status, error.StatusCode);
            AssertEqual(true, error.Message.Contains("/api/"));
            AssertEqual(status is null ? 6 : 3, requests);
        }
    }
}

static async Task TestSunshineCloseCurrentApplication()
{
    using var httpClient = new HttpClient(new FakeHttpHandler((request, _) =>
    {
        AssertEqual(HttpMethod.Post, request.Method);
        AssertEqual("/api/apps/close", request.RequestUri?.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":true}", Encoding.UTF8, "application/json")
        });
    }));
    using var client = new SunshineApiClient(httpClient, new Uri("https://localhost:47990/"));

    await client.CloseCurrentApplicationAsync("admin", "secret");
}

static async Task TestSunshinePairing()
{
    string? requestBody = null;
    using var httpClient = new HttpClient(new FakeHttpHandler(async (request, cancellationToken) =>
    {
        AssertEqual(HttpMethod.Post, request.Method);
        AssertEqual("/api/pin", request.RequestUri?.AbsolutePath);
        requestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }));
    using var client = new SunshineApiClient(httpClient, new Uri("https://localhost:47990/"));

    await client.SubmitPinAsync("1234", "PiuLink tablet", "admin", "secret");

    if (requestBody is null ||
        !requestBody.Contains("\"pin\":\"1234\"", StringComparison.Ordinal) ||
        !requestBody.Contains("\"name\":\"PiuLink tablet\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Unexpected pairing payload: {requestBody}");
    }
}

static Task TestSunshinePinValidation()
{
    AssertEqual(true, SunshineApiClient.IsValidPairingPin("1234"));
    AssertEqual(false, SunshineApiClient.IsValidPairingPin("123"));
    AssertEqual(false, SunshineApiClient.IsValidPairingPin("12A4"));
    AssertEqual(false, SunshineApiClient.IsValidPairingPin(null));
    return Task.CompletedTask;
}

static Task TestSunshineDisplayParsing()
{
    const string logs = """
        [2026-08-17]: Info: Currently available display devices:
        [
          {
            "device_id": "{13c4fd90-9e4f-5fbf-a267-5ae97932de5e}",
            "display_name": "\\\\.\\DISPLAY2",
            "edid": { "manufacturer_id": "HKC" },
            "friendly_name": "G27M7Pro",
            "info": {
              "primary": true,
              "refresh_rate": { "value": { "denominator": 1, "numerator": 160 } },
              "resolution": { "height": 2160, "width": 3840 }
            }
          },
          {
            "device_id": "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}",
            "display_name": "\\\\.\\DISPLAY5",
            "edid": { "manufacturer_id": "MTT" },
            "friendly_name": "VDD by MTT",
            "info": {
              "primary": false,
              "refresh_rate": { "value": { "denominator": 1000, "numerator": 59940 } },
              "resolution": { "height": 1080, "width": 1920 }
            }
          }
        ]
        [2026-08-17]: Info: Startup complete
        """;

    var displays = SunshineApiClient.ParseDisplays(logs);
    AssertEqual(2, displays.Count);
    AssertEqual("G27M7Pro", displays[0].FriendlyName);
    AssertEqual(true, displays[0].IsPrimary);
    AssertEqual(false, displays[0].IsVirtual);
    AssertEqual(3840, displays[0].Width);
    AssertEqual(true, displays[1].IsVirtual);
    AssertEqual(59.94, Math.Round(displays[1].RefreshRate ?? 0, 2));
    return Task.CompletedTask;
}

static Task TestSunshineLatestDisplayParsing()
{
    const string logs = """
        Info: Currently available display devices:
        [
          {
            "device_id": "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}",
            "display_name": "\\\\.\\DISPLAY5",
            "edid": { "manufacturer_id": "MTT" },
            "friendly_name": "VDD by MTT",
            "info": { "primary": false }
          }
        ]
        Info: Currently available devices:
        [
          {
            "device_id": "{13c4fd90-9e4f-5fbf-a267-5ae97932de5e}",
            "display_name": "\\\\.\\DISPLAY2",
            "edid": { "manufacturer_id": "HKC" },
            "friendly_name": "G27M7Pro",
            "info": { "primary": true }
          }
        ]
        Error: Device "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}" is not available in the system!
        """;

    var displays = SunshineApiClient.ParseDisplays(logs);
    AssertEqual(1, displays.Count);
    AssertEqual("G27M7Pro", displays[0].FriendlyName);
    AssertEqual(false, displays[0].IsVirtual);
    return Task.CompletedTask;
}

static Task TestSunshineDuplicateDisplayParsing()
{
    const string logs = """
        Info: Currently available display devices:
        [
          {
            "device_id": "{physical-old}",
            "display_name": "\\\\.\\DISPLAY1",
            "friendly_name": "Main monitor",
            "info": { "primary": false }
          },
          {
            "device_id": "{physical-current}",
            "display_name": "\\\\.\\DISPLAY1",
            "friendly_name": "Main monitor",
            "info": { "primary": true, "resolution": { "width": 2560, "height": 1440 } }
          },
          {
            "device_id": "{vdd-old}",
            "display_name": "",
            "friendly_name": "VDD by MTT"
          },
          {
            "device_id": "{vdd-selected}",
            "display_name": "",
            "friendly_name": "VDD by MTT"
          },
          {
            "device_id": "{vdd-selected}",
            "display_name": "",
            "friendly_name": "VDD by MTT"
          }
        ]
        """;

    var displays = SunshineApiClient.ParseDisplays(logs, "{vdd-selected}");
    AssertEqual(4, displays.Count);
    AssertEqual(2, displays.Count(display => display.IsVirtual));
    AssertEqual(2, displays.Count(display => !display.IsVirtual));
    AssertEqual(1, displays.Count(display => display.DeviceId == "{vdd-selected}"));
    return Task.CompletedTask;
}

static async Task TestSunshineDisplayRouting()
{
    var calls = new List<string>();
    string? savedConfiguration = null;
    using var httpClient = new HttpClient(new FakeHttpHandler(async (request, cancellationToken) =>
    {
        calls.Add($"{request.Method} {request.RequestUri?.AbsolutePath}");
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/config")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    savedConfiguration ??
                    "{\"status\":true,\"platform\":\"windows\",\"version\":\"test\",\"system_tray\":\"false\",\"output_name\":\"{old}\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/config")
        {
            savedConfiguration = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":true}", Encoding.UTF8, "application/json")
        };
    }));
    using var client = new SunshineApiClient(httpClient, new Uri("https://localhost:47990/"));

    await client.ApplyDisplaySettingsAsync(
        "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}",
        exclusiveDisplay: true,
        smartDisplay: true,
        "admin",
        "secret");

    AssertEqual(
        "GET /api/config|POST /api/config|POST /api/restart|GET /api/config",
        string.Join('|', calls));
    if (savedConfiguration is null ||
        !savedConfiguration.Contains("\"system_tray\":\"false\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"output_name\":\"{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"dd_configuration_option\":\"ensure_only_display\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"dd_resolution_option\":\"auto\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"dd_refresh_rate_option\":\"auto\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"dd_hdr_option\":\"auto\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"dd_config_revert_on_disconnect\":\"true\"", StringComparison.Ordinal) ||
        savedConfiguration.Contains("\"platform\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Unexpected display configuration: {savedConfiguration}");
    }

    await client.ApplyDisplaySettingsAsync(
        "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}",
        exclusiveDisplay: true,
        smartDisplay: false,
        "admin",
        "secret");
    if (savedConfiguration is null ||
        !savedConfiguration.Contains("\"dd_resolution_option\":\"disabled\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"dd_refresh_rate_option\":\"disabled\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"dd_hdr_option\":\"auto\"", StringComparison.Ordinal))
        throw new InvalidOperationException("Fixed resolution disabled client requested HDR.");
}

static async Task TestSunshineRestartDisconnect()
{
    const string deviceId = "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}";
    var configurationSaved = false;
    using var httpClient = new HttpClient(new FakeHttpHandler((request, _) =>
    {
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/config")
        {
            var payload = configurationSaved
                ? $"{{\"output_name\":\"{deviceId}\",\"dd_configuration_option\":\"ensure_active\",\"dd_resolution_option\":\"auto\",\"dd_refresh_rate_option\":\"auto\",\"dd_hdr_option\":\"auto\",\"dd_config_revert_on_disconnect\":\"true\"}}"
                : "{\"system_tray\":\"false\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }

        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/config")
        {
            configurationSaved = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/restart")
        {
            throw new HttpRequestException(
                HttpRequestError.ResponseEnded,
                "Sunshine closed the restart response.",
                new HttpIOException(HttpRequestError.ResponseEnded, "Response ended prematurely."));
        }

        throw new InvalidOperationException("Unexpected Sunshine API request.");
    }));
    using var client = new SunshineApiClient(httpClient, new Uri("https://localhost:47990/"));

    await client.ApplyDisplaySettingsAsync(
        deviceId,
        exclusiveDisplay: false,
        smartDisplay: true,
        "admin",
        "secret");
}

static async Task TestSunshineCompleteConfiguration()
{
    var calls = new List<string>();
    string? savedConfiguration = null;
    using var httpClient = new HttpClient(new FakeHttpHandler(async (request, cancellationToken) =>
    {
        calls.Add($"{request.Method} {request.RequestUri?.AbsolutePath}");
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/config")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":true,\"platform\":\"windows\",\"version\":\"test\",\"upnp\":\"enabled\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/config")
        {
            savedConfiguration = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":true}", Encoding.UTF8, "application/json")
        };
    }));
    using var client = new SunshineApiClient(httpClient, new Uri("https://localhost:47990/"));

    var loaded = await client.GetConfigurationJsonAsync("admin", "secret");
    if (!loaded.Contains("\"upnp\"", StringComparison.Ordinal) ||
        loaded.Contains("\"status\"", StringComparison.Ordinal) ||
        loaded.Contains("\"platform\"", StringComparison.Ordinal) ||
        loaded.Contains("\"version\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Unexpected complete configuration: {loaded}");
    }

    await client.ApplyConfigurationJsonAsync(
        "{\"status\":false,\"platform\":\"ignored\",\"upnp\":\"disabled\",\"future_option\":42}",
        "admin",
        "secret");
    AssertEqual("GET /api/config|POST /api/config|POST /api/restart", string.Join('|', calls));
    if (savedConfiguration is null ||
        savedConfiguration.Contains("\"status\"", StringComparison.Ordinal) ||
        savedConfiguration.Contains("\"platform\"", StringComparison.Ordinal) ||
        !savedConfiguration.Contains("\"future_option\":42", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Unexpected saved configuration: {savedConfiguration}");
    }
}

static async Task TestIdleConfiguration()
{
    foreach (var (streams, app) in new[] { (0, 0), (1, 2), (0, 2) })
    {
        string? id = null, owner = null;
        bool released = false, wrote = false, restarted = false;
        using var http = new HttpClient(new FakeHttpHandler(async (request, ct) => {
            var path = request.RequestUri!.AbsolutePath;
            var json = "{}";
            switch (path) {
                case "/api/piulink/prepared":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct))) {
                        var lease = body.RootElement;
                        if (id is null) {
                            id = lease.GetProperty("requestId").GetString();
                            owner = lease.GetProperty("owner").GetString();
                            AssertEqual(false, lease.GetProperty("ready").GetBoolean());
                        } else {
                            AssertEqual(id, lease.GetProperty("requestId").GetString());
                            AssertEqual(owner, lease.GetProperty("owner").GetString());
                            AssertEqual("abort", lease.GetProperty("action").GetString());
                            released = true;
                        }
                    }
                    break;
                case "/api/piulink/state": json = JsonSerializer.Serialize(new { activeStreams = streams, currentApplicationId = app }); break;
                case "/api/apps": json = "{\"apps\":[]}"; break;
                case "/api/clients/list": json = "{\"named_certs\":[]}"; break;
                case "/api/config": wrote = true; break;
                case "/api/restart": restarted = true; break;
                default: throw new Exception(path);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        using var api = new SunshineApiClient(http, new Uri("https://localhost/"));
        var idle = streams == 0 && app == 0;
        bool blocked = false;
        try { await api.ApplyIdleConfigurationJsonAsync("{}", "test", "fixture"); }
        catch (InvalidOperationException) when (!idle) { blocked = true; }
        AssertEqual(!idle, blocked);
        AssertEqual(idle, wrote);
        AssertEqual(idle, restarted);
        AssertEqual(true, released);
    }
}

static async Task TestRestoreConfiguration()
{
    string? payload = null;
    int restarts = 0;
    using var http = new HttpClient(new FakeHttpHandler(async (request, ct) => {
        var path = request.RequestUri!.AbsolutePath;
        var response = "{}";
        if (path == "/api/config" && request.Method == HttpMethod.Get)
            response = "{\"output_name\":\"virtual\",\"dd_hdr_option\":\"auto\",\"custom_option\":7}";
        else if (path == "/api/config") payload = await request.Content!.ReadAsStringAsync(ct);
        else if (path == "/api/restart") restarts++;
        else throw new Exception(path);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
    }));
    using var api = new SunshineApiClient(http, new Uri("https://localhost/"));
    const string before = "{\"output_name\":\"physical\",\"custom_option\":1}";
    AssertEqual(false, await api.RestoreDisplayConfigurationAsync(before, "test", "fixture", false));
    AssertEqual(true, payload is null && restarts == 0);
    AssertEqual(true, await api.RestoreDisplayConfigurationAsync(before, "test", "fixture", true));
    using var saved = JsonDocument.Parse(payload!);
    AssertEqual("physical", saved.RootElement.GetProperty("output_name").GetString());
    AssertEqual(7, saved.RootElement.GetProperty("custom_option").GetInt32());
    AssertEqual(false, saved.RootElement.TryGetProperty("dd_hdr_option", out _));
    AssertEqual(1, restarts);
}

static Task TestSessionGuardianGracePeriod()
{
    var guardian = new SunshineSessionGuardian(TimeSpan.FromSeconds(10));
    var startedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    var retained = new SunshineOverview(2, 1, 7, 0);

    var first = guardian.Observe(retained, startedAt);
    var waiting = guardian.Observe(retained, startedAt.AddSeconds(9));
    var recover = guardian.Observe(retained, startedAt.AddSeconds(10));

    AssertEqual(SunshineSessionGuardState.GracePeriod, first.State);
    AssertEqual(SunshineSessionGuardState.GracePeriod, waiting.State);
    AssertEqual(SunshineSessionGuardState.RecoveryRequired, recover.State);
    AssertEqual(10d, recover.RetainedFor.TotalSeconds);
    return Task.CompletedTask;
}

static Task TestSessionGuardianActiveReset()
{
    var guardian = new SunshineSessionGuardian(TimeSpan.FromSeconds(10));
    var startedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    var retained = new SunshineOverview(2, 1, 7, 0);
    var active = new SunshineOverview(2, 1, 7, 1);

    guardian.Observe(retained, startedAt);
    AssertEqual(
        SunshineSessionGuardState.ActiveStream,
        guardian.Observe(active, startedAt.AddSeconds(8)).State);
    AssertEqual(
        SunshineSessionGuardState.GracePeriod,
        guardian.Observe(retained, startedAt.AddSeconds(11)).State);
    AssertEqual(
        SunshineSessionGuardState.GracePeriod,
        guardian.Observe(retained, startedAt.AddSeconds(18)).State);
    return Task.CompletedTask;
}

static async Task TestTailscaleGuidedSignIn()
{
    const string status = "{\"BackendState\":\"NeedsLogin\",\"AuthURL\":\"https://login.tailscale.com/a/piulink-test\"}";
    var runner = new RecordingRunner(new ProcessResult(0, status, string.Empty, false));
    var service = new TailscaleControlService(runner, () => "tailscale.exe");

    var result = await service.ConnectAsync();

    AssertEqual(TailscaleConnectState.AwaitingAuthentication, result.State);
    AssertEqual("https://login.tailscale.com/a/piulink-test", result.AuthenticationUri?.AbsoluteUri);
    AssertEqual("tailscale.exe", runner.FileName);
    AssertEqual("status --json --peers=false", string.Join(' ', runner.Arguments));
}

static async Task TestTailscaleAuthUrlGeneration()
{
    const string pending = "{\"BackendState\":\"NeedsLogin\"}";
    const string ready = "{\"BackendState\":\"NeedsLogin\",\"AuthURL\":\"https://login.tailscale.com/a/generated\"}";
    var runner = new SequenceRunner(
        new ProcessResult(0, pending, string.Empty, false),
        new ProcessResult(1, string.Empty, "timeout waiting for Tailscale service", false),
        new ProcessResult(0, ready, string.Empty, false));
    var service = new TailscaleControlService(runner, () => "tailscale.exe");

    var result = await service.ConnectAsync();

    AssertEqual(TailscaleConnectState.AwaitingAuthentication, result.State);
    AssertEqual("https://login.tailscale.com/a/generated", result.AuthenticationUri?.AbsoluteUri);
    AssertEqual("status --json --peers=false|up --timeout=2s|status --json --peers=false", string.Join('|', runner.Calls));
}

static async Task TestTailscaleIncompleteStatusRetry()
{
    const string pending = "{\"BackendState\":\"NeedsLogin\"}";
    const string ready = "{\"BackendState\":\"NeedsLogin\",\"AuthURL\":\"https://login.tailscale.com/a/recovered\"}";
    var runner = new SequenceRunner(
        new ProcessResult(-1, "{", string.Empty, true),
        new ProcessResult(0, pending, string.Empty, false),
        new ProcessResult(1, string.Empty, "timeout waiting for Tailscale service", false),
        new ProcessResult(0, ready, string.Empty, false));
    var service = new TailscaleControlService(runner, () => "tailscale.exe");

    var result = await service.ConnectAsync();

    AssertEqual(TailscaleConnectState.AwaitingAuthentication, result.State);
    AssertEqual("https://login.tailscale.com/a/recovered", result.AuthenticationUri?.AbsoluteUri);
    AssertEqual(
        "status --json --peers=false|status --json --peers=false|up --timeout=2s|status --json --peers=false",
        string.Join('|', runner.Calls));
}

static async Task TestTailscaleInlineAuthUrl()
{
    const string pending = "{\"BackendState\":\"NeedsLogin\"}";
    var runner = new SequenceRunner(
        new ProcessResult(0, pending, string.Empty, false),
        new ProcessResult(
            1,
            "To authenticate, visit: https://login.tailscale.com/a/from-cli",
            "context deadline exceeded",
            false));
    var service = new TailscaleControlService(runner, () => "tailscale.exe");

    var result = await service.ConnectAsync();

    AssertEqual(TailscaleConnectState.AwaitingAuthentication, result.State);
    AssertEqual("https://login.tailscale.com/a/from-cli", result.AuthenticationUri?.AbsoluteUri);
    AssertEqual(
        "status --json --peers=false|up --timeout=2s",
        string.Join('|', runner.Calls));
}

static async Task TestTailscalePreferences()
{
    const string json = """
        {
          "RouteAll": true,
          "CorpDNS": true,
          "ShieldsUp": false,
          "RunSSH": true,
          "RunWebClient": false,
          "WantRunning": true,
          "AutoUpdate": { "Check": true, "Apply": true }
        }
        """;
    var runner = new SequenceRunner(
        new ProcessResult(0, json, string.Empty, false),
        new ProcessResult(0, string.Empty, string.Empty, false));
    var service = new TailscaleControlService(runner, () => "tailscale.exe");

    var preferences = await service.GetPreferencesAsync();
    AssertEqual(true, preferences.AcceptRoutes);
    AssertEqual(true, preferences.AcceptDns);
    AssertEqual(false, preferences.ShieldsUp);
    AssertEqual(true, preferences.RunSsh);
    AssertEqual(false, preferences.RunWebClient);
    AssertEqual(true, preferences.AutoUpdate);
    AssertEqual(true, preferences.WantRunning);

    await service.ApplyPreferencesAsync(preferences with { RunSsh = false, RunWebClient = true });
    AssertEqual(
        "debug prefs|set --accept-routes=true --accept-dns=true --shields-up=false --ssh=false --webclient=true --auto-update=true",
        string.Join('|', runner.Calls));
}

static async Task TestBundledSunshinePayload()
{
    var directory = Path.Combine(Path.GetTempPath(), $"piulink-sunshine-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        const string payloadName = "Sunshine.msi";
        const string licenseName = "GPL-3.0.txt";
        const string licenseText = "GPLv3 test fixture";
        await File.WriteAllTextAsync(Path.Combine(directory, payloadName), "PiuLink");
        await File.WriteAllTextAsync(Path.Combine(directory, licenseName), licenseText);
        var licenseHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(licenseText)));
        var manifest = new BundledComponentManifest(
            1,
            "LizardByte.Sunshine",
            "1.0.0",
            "x64",
            payloadName,
            new Uri("https://example.test/Sunshine.msi"),
            "86E736308586C8C000BD32FE4ADAB4A55406D95A58A80863AE29D9652C93AD1F",
            "CN=Sunshine Test",
            new string('A', 40),
            "GPL-3.0-only",
            licenseName,
            licenseHash,
            new Uri("https://example.test/license"),
            "v1.0.0",
            new string('B', 40),
            new Uri("https://example.test/source"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
        var service = new SunshineInstallerService(
            directory,
            manifest,
            (_, subject, thumbprint) => new AuthenticodeVerificationResult(
                true,
                subject,
                thumbprint,
                "Valid test signature."));

        var ready = await service.InspectAsync();
        AssertEqual(BundledInstallerState.Ready, ready.State);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest with { Version = "1.0.1" }));
        var changedManifest = await service.InspectAsync();
        AssertEqual(BundledInstallerState.Invalid, changedManifest.State);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
        await File.AppendAllTextAsync(Path.Combine(directory, payloadName), "tampered");
        var tampered = await service.InspectAsync();
        AssertEqual(BundledInstallerState.Invalid, tampered.State);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

file sealed class FakeRunner(ProcessResult result) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) => Task.FromResult(result);
}

file sealed class FakeHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => send(request, cancellationToken);
}

file sealed class RecordingRunner(ProcessResult result) : IProcessRunner
{
    public string? FileName { get; private set; }

    public IReadOnlyList<string> Arguments { get; private set; } = [];

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        FileName = fileName;
        Arguments = arguments;
        return Task.FromResult(result);
    }
}

file sealed class SequenceRunner(params ProcessResult[] results) : IProcessRunner
{
    private readonly Queue<ProcessResult> _results = new(results);

    public List<string> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(string.Join(' ', arguments));
        if (_results.Count == 0)
        {
            throw new InvalidOperationException("No queued process result remains.");
        }

        return Task.FromResult(_results.Dequeue());
    }
}
