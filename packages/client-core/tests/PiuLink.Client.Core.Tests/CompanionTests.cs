using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Storage;
using PiuLink.Client.Core.Streaming;

internal static class CompanionTests
{
    public static async Task Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PiuLink.sln"))) root = root.Parent;
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root!.FullName, "contracts", "companion-v1.json")))!;
        var inventory = HostCompanionClient.ParseInventory(fixture["inventory"]!.ToJsonString());
        var unnamed = fixture["inventory"]!.DeepClone();
        unnamed["displays"]![0]!["name"] = "";
        Check(HostCompanionClient.ParseInventory(unnamed.ToJsonString()).Displays[0].Name == "Display 1",
            "an unnamed physical monitor must remain available");
        Check(inventory.Displays.Length == 3 && inventory.Displays[0].Name == inventory.Displays[1].Name &&
            inventory.Displays[0].DeviceId != inventory.Displays[1].DeviceId, "same-name monitors must remain distinct");
        Check(inventory.Displays[2].Width is null && inventory.VirtualRefreshRates.Contains(120), "nullable virtual modes");
        Check(inventory.SupportsWindowGathering, "Host advertises physical window gathering");
        Check(inventory.SupportsDisplayScaling && inventory.Displays[2].ScaleOptions!.Length == 0,
            "Unknown DPI capability must not advertise guessed options");
        var duplicateDevice = fixture["inventory"]!.DeepClone();
        duplicateDevice["displays"]![1]!["deviceId"] = duplicateDevice["displays"]![0]!["deviceId"]!.GetValue<string>();
        RejectInventory(duplicateDevice, "duplicate device IDs must not become ambiguous display choices");
        var invalidRate = fixture["inventory"]!.DeepClone();
        invalidRate["virtualRefreshRates"]![0] = 0;
        RejectInventory(invalidRate, "invalid virtual refresh rates must not become selectable settings");
        var missingModes = fixture["inventory"]!.DeepClone();
        missingModes["virtualResolutions"] = new JsonArray();
        Check(HostCompanionClient.ParseInventory(missingModes.ToJsonString()).Displays[2].Modes!.Length > 0,
            "Per-target modes remain usable without global compatibility arrays");
        var missingDisplays = fixture["inventory"]!.DeepClone();
        missingDisplays["displays"] = new JsonArray();
        RejectInventory(missingDisplays, "an empty display inventory must not produce an ambiguous automatic target");
        var gatherProfile = StreamProfile.Default with { GatherWindows = true };
        Check(HostCompanionClient.Selection(inventory.Displays[0], gatherProfile)["gatherWindows"] is true,
            "physical gathering must reach Host");
        Check(!HostCompanionClient.Selection(inventory.Displays[2], gatherProfile).ContainsKey("gatherWindows"),
            "virtual mode must not gather onto a physical screen");
        var scaleProfile = StreamProfile.Default with { DisplayScalePercent = 150 };
        Check(HostCompanionClient.Selection(inventory.Displays[0], scaleProfile)["scalePercent"] is 150,
            "Windows display scale must reach Host");
        Check(!HostCompanionClient.Selection(null, scaleProfile).ContainsKey("scalePercent"),
            "automatic selection without a resolved display must not send an ambiguous scale target");
        // The contract uses an explicit virtual mode; it must not silently
        // depend on whatever the interactive client currently defaults to.
        var contractProfile = StreamProfile.Default with { Resolution = "1920x1080" };
        HostDisplay?[] choices = [inventory.Displays[2], inventory.Displays[0], null];
        for (int i = 0; i < choices.Length; i++)
            Check(JsonNode.DeepEquals(fixture["selectionRequests"]![i], JsonSerializer.SerializeToNode(
                HostCompanionClient.Selection(choices[i], contractProfile))), "request contract " + i);
        var endpoint = HostEndpoint.Parse("[fd7a:115c:a1e0::1]");
        Check(HostCompanionClient.Endpoint(endpoint, "/v1/displays").AbsoluteUri ==
            "http://[fd7a:115c:a1e0::1]:48011/v1/displays", "IPv6 companion URL");
        int calls = 0;
        using var http = new HttpClient(new Handler(async request => {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            Check(Guid.TryParse(body["requestId"]?.GetValue<string>(), out _), "opaque preparation request ID");
            body.Remove("requestId");
            Check(JsonNode.DeepEquals(body, fixture["selectionRequests"]![0]), "wire body");
            return ++calls == 1 ? new(HttpStatusCode.Conflict) : new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ready\"}") };
        }));
        using var client = new HostCompanionClient(http);
        await client.PrepareAsync(endpoint, choices[0], contractProfile, default);
        Check(calls == 2, "busy host retry");
        using var retainedHttp = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) {
            Content = new StringContent("{\"error\":\"retained_application\"}") })));
        using var retainedClient = new HostCompanionClient(retainedHttp);
        try { await retainedClient.PrepareAsync(endpoint, null, StreamProfile.Default, default); throw new Exception("Retained session was not reported"); }
        catch (RetainedDisplayRouteException) { }
        foreach (var body in new[] { "{}", "{\"status\":\"error\"}", new string('x', 4097) }) {
            using var badHttp = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
            using var badClient = new HostCompanionClient(badHttp);
            try { await badClient.PrepareAsync(endpoint, null, StreamProfile.Default, default); throw new Exception("Invalid response accepted"); }
            catch (IOException) { }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.PrepareAsync(endpoint, null, StreamProfile.Default, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }

        var file = Path.Combine(Path.GetTempPath(), "piulink-parity-" + Guid.NewGuid() + ".json");
        try {
            var repo = new JsonHostRepository(file);
            var old = new HostProfile(Guid.NewGuid(), "desktop", "host.local", DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new[] { new { old.Id, old.Name, old.Address, old.CreatedAt } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Check((await repo.LoadAsync())[0].StreamSettings is null, "legacy settings migration");
            var updated = old with { StreamSettings = StreamProfile.Default with { FramesPerSecond = 120 }, DisplayId = choices[0]!.DeviceId, RecentApplications = ["Desktop"] };
            await repo.SaveAsync([updated]);
            var loaded = (await repo.LoadAsync())[0];
            Check(loaded.StreamSettings == updated.StreamSettings && loaded.DisplayId == updated.DisplayId && loaded.RecentApplications!.SequenceEqual(updated.RecentApplications), "per-host preferences roundtrip");
        } finally { File.Delete(file); }
    }
    public static async Task DiscoverMixedResponses()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(request.RequestUri!.Host == "127.0.0.2" ? "unrelated device" :
                "<root><appversion>7.1</appversion><hostname>Test Host</hostname></root>") })));
        var hosts = await LanHostDiscoveryService.ProbeAddressesAsync(http, ["127.0.0.2", "127.0.0.3"], default);
        Check(hosts.Count == 1 && hosts[0] == new DiscoveredHost("Test Host", "127.0.0.3"), "Mixed discovery results");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        try { await LanHostDiscoveryService.ProbeAddressesAsync(http, ["127.0.0.3"], stopped.Token); throw new Exception("Discovery ignored cancellation"); }
        catch (OperationCanceledException) { }
    }

    public static async Task VerifyBugfixContracts()
    {
        GameStreamHttpClient.EnsureXmlStatusOk("<root status_code=\"200\"/>");
        GameStreamHttpClient.EnsureXmlStatusOk("<root/>");
        foreach (var status in new[] { "400", "409" })
        {
            try
            {
                GameStreamHttpClient.EnsureXmlStatusOk($"<root status_code=\"{status}\" status_message=\"rejected\"/>");
                throw new Exception($"Expected status_code {status} to throw");
            }
            catch (IOException) { }
        }
        Check(!GameStreamHttpClient.IsQuitAccepted("<root/>"), "missing <cancel> must not count as quit success");
        Check(!GameStreamHttpClient.IsQuitAccepted("<root><cancel>0</cancel></root>"), "cancel=0 must fail");
        Check(GameStreamHttpClient.IsQuitAccepted("<root><cancel>1</cancel></root>"), "cancel=1 must succeed");

        var modeCalls = 0;
        using var modeHttp = new HttpClient(new Handler(_ =>
        {
            modeCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"error\":\"display_mode_unavailable\"}")
            });
        }));
        using var modeClient = new HostCompanionClient(modeHttp);
        try
        {
            await modeClient.PrepareAsync(HostEndpoint.Parse("127.0.0.1"), null, StreamProfile.Default, default);
            throw new Exception("display_mode_unavailable must fail fast");
        }
        catch (IOException) { }
        Check(modeCalls == 1, "display_mode_unavailable must not retry 40 times");

        var oversized = new string('A', 40 * 1024);
        using var discoveryHttp = new HttpClient(new Handler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host == "127.0.0.2"
                ? $"<root><appversion>7.1</appversion><hostname>{oversized}</hostname></root>"
                : "<root><appversion>7.1</appversion><hostname>Bounded Host</hostname></root>")
        })));
        var discovered = await LanHostDiscoveryService.ProbeAddressesAsync(discoveryHttp, ["127.0.0.2", "127.0.0.3"], default);
        Check(discovered.Count == 1 && discovered[0] == new DiscoveredHost("Bounded Host", "127.0.0.3"),
            "oversized discovery response must be skipped");

        var idDir = Path.Combine(Path.GetTempPath(), "piulink-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var created = GameStreamIdentity.LoadOrCreate(idDir))
            {
                Check(!string.IsNullOrWhiteSpace(created.UniqueId) && created.PemCertificate.Length > 0, "created identity must be valid");
            }
            using var reloaded = GameStreamIdentity.LoadOrCreate(idDir);
            Check(!string.IsNullOrWhiteSpace(reloaded.UniqueId), "reloaded identity must be valid");
        }
        finally { if (Directory.Exists(idDir)) Directory.Delete(idDir, true); }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void RejectInventory(JsonNode inventory, string message)
    {
        try
        {
            HostCompanionClient.ParseInventory(inventory.ToJsonString());
            throw new Exception(message);
        }
        catch (IOException) { }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return send(request); }
    }
}
