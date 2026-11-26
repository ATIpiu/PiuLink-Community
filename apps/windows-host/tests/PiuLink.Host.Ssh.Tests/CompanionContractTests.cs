using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PiuLink.Host.Core.Sunshine;
using PiuLink.Host.Services;

internal static class CompanionContractTests
{
    private static readonly X509Certificate2 ServerCertificate = Certificate("Host");
    private static readonly X509Certificate2 ClientCertificate = Certificate("Paired Pad");
    internal static X509Certificate2 Certificate(string name) {
        using var key = RSA.Create(2048);
        using var pem = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(pem.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
    public static async Task Run(string fixturePath, bool export)
    {
        const string virtualId = "33333333-3333-3333-3333-333333333333";
        var displays = new[] {
            new SunshineDisplay("11111111-1111-1111-1111-111111111111", "TESTDISPLAY1", "显示器", true, false, 1920, 1080, 60) { Bounds = new(-1920, 120, 1920, 1080) },
            new SunshineDisplay("22222222-2222-2222-2222-222222222222", "TESTDISPLAY2", "显示器", false, false, 2560, 1440, 60) { Bounds = new(0, 0, 2560, 1440) },
            new SunshineDisplay(virtualId, "TESTDISPLAY3", "PiuLink Virtual", false, true, null, null, null)
        };
        int selected = 0;
        string? pairedName = null;
        string? authenticatedPeer = null;
        using var transfer = new DisplayCompanionServer(_ => Task.FromResult<SunshineDisplayState?>(null),
            (_, _, _, _, _) => Task.FromResult(true), pairFileTransfer: (request, _) => {
                pairedName = request.DeviceName;
                authenticatedPeer = request.CertificateFingerprint;
                return Task.FromResult<FileTransferPairResponse?>(new("paired", "test-token", 48012));
            });
        const string legacy = "{\"deviceId\":\"android-test-device\",\"deviceName\":\"我的手机 📱\"}";
        if ((await Request(transfer, "POST", "/v1/transfer/pair", legacy)).Code != 409 || authenticatedPeer is not null)
            throw new Exception("Unversioned transfer pairing should request an update");
        var pairResult = await Request(transfer, "POST", "/v1/transfer/pair",
            "{\"deviceId\":\"android-test-device\",\"deviceName\":\"我的手机 📱\",\"transport\":\"tls-v2\"}");
        if (pairResult.Code != 200 || pairedName != "我的手机 📱" || pairResult.Body["transport"]?.GetValue<string>() != "tls-v2" ||
            authenticatedPeer != Convert.ToHexString(SHA256.HashData(ClientCertificate.RawData)))
            throw new Exception("Authenticated certificate did not reach transfer pairing");
        int? selectedScale = null;
        using var server = new DisplayCompanionServer(_ => Task.FromResult<SunshineDisplayState?>(new(displays, virtualId, true, true)),
            (id, exclusive, width, height, gather, scale, _) => {
                selected++; selectedScale = scale; return Task.FromResult(true);
            });
        var denied = await Request(server, "POST", "/v1/displays/select", "{\"exclusive\":false}", secure: false);
        if (denied.Code != 403 || selected != 0) throw new Exception("Unauthenticated request changed the desktop");
        server.ReadModes = _ => [new(1920, 1080, 60), new(1920, 1080, 120), new(2560, 1440, 60), new(2560, 1440, 120)];
        var inventory = await Request(server, "GET", "/v1/displays", null);
        inventory.Body.Remove("observedAt"); inventory.Body.Remove("topologyEpoch");
        if (inventory.Code != 200) throw new Exception("Inventory contract failed");
        string[] requests = [
            "{\"deviceId\":\"" + virtualId + "\",\"exclusive\":true,\"width\":1920,\"height\":1080,\"fps\":60}",
            "{\"deviceId\":\"11111111-1111-1111-1111-111111111111\",\"exclusive\":false}",
            "{\"deviceId\":null,\"exclusive\":false}"
        ];
        JsonObject? ready = null;
        foreach (var request in requests) {
            var response = await Request(server, "POST", "/v1/displays/select", request);
            if (response.Code != 200 || response.Body["status"]?.GetValue<string>() != "ready") throw new Exception("Valid Android selection rejected");
            ready = response.Body; ready.Remove("requestId"); ready.Remove("expiresAt");
        }
        if (selected != 3) throw new Exception("Selection callback was not invoked");
        var scaleResponse = await Request(server, "POST", "/v1/displays/select",
            "{\"deviceId\":\"" + virtualId + "\",\"exclusive\":true,\"width\":1920,\"height\":1080,\"scalePercent\":150}");
        if (scaleResponse.Code != 200 || selectedScale != 150) throw new Exception("Windows scale was not forwarded");
        string[] invalid = ["[]", "{}", "{\"exclusive\":\"false\"}", "{\"exclusive\":false,\"deviceId\":123}",
            "{\"exclusive\":false,\"width\":0,\"height\":0}", "{\"exclusive\":false,\"width\":\"1920\",\"height\":\"1080\"}",
            "{\"exclusive\":true,\"deviceId\":null}", "{\"exclusive\":false,\"width\":1920}",
            "{\"exclusive\":false,\"width\":1920,\"height\":1080,\"fps\":60}",
            "{\"deviceId\":\"11111111-1111-1111-1111-111111111111\",\"exclusive\":false,\"scalePercent\":110}",
            "{\"exclusive\":false,\"scalePercent\":150}"];
        foreach (var request in invalid) {
            if ((await Request(server, "POST", "/v1/displays/select", request)).Code != 400)
                throw new Exception("Malformed selection was accepted: " + request);
        }
        if (selected != 4) throw new Exception("Invalid input changed display state");
        var gatherRequest = "{\"deviceId\":\"11111111-1111-1111-1111-111111111111\",\"exclusive\":false,\"gatherWindows\":true}";
        bool gathered = false;
        using var physicalOnly = new DisplayCompanionServer(_ => Task.FromResult<SunshineDisplayState?>(new(displays[..2], null, false, false)),
            (_, _, _, _, gather, _) => { gathered = gather; return Task.FromResult(true); });
        if ((await Request(physicalOnly, "POST", "/v1/displays/select", gatherRequest)).Code != 200 || !gathered)
            throw new Exception("Physical-only window gathering failed");
        foreach (var request in new[] { "{\"exclusive\":false,\"gatherWindows\":true}",
            "{\"exclusive\":false,\"gatherWindows\":\"true\"}" })
            if ((await Request(physicalOnly, "POST", "/v1/displays/select", request)).Code != 400)
                throw new Exception("Invalid gathering request accepted");
        using var busy = new DisplayCompanionServer(_ => Task.FromResult<SunshineDisplayState?>(null), (_, _, _, _, _) => Task.FromResult(false));
        if ((await Request(busy, "POST", "/v1/displays/select", requests[0])).Code != 409) throw new Exception("Busy contract changed");
        using var broken = new DisplayCompanionServer(_ => Task.FromResult<SunshineDisplayState?>(null), (_, _, _, _, _) => throw new IOException("test"));
        if ((await Request(broken, "POST", "/v1/displays/select", requests[0])).Code != 503) throw new Exception("Failure contract changed");
        var session = Guid.NewGuid().ToString();
        var key = new string('A', 64);
        string captureKey = new string('B', 64);
        int count = 1, mutations = 0;
        bool enabled = false;
        string Body(string action, string? output = null, int? expected = null, string? id = null) =>
            System.Text.Json.JsonSerializer.Serialize(new { sessionId = id ?? session, action, key = output, expectedCount = expected });
        physicalOnly.SessionDisplays = (owner, cmd, cancellationToken) => {
            if (owner != Convert.ToHexString(SHA256.HashData(ClientCertificate.RawData)) || cmd.SessionId != session)
                throw new UnauthorizedAccessException();
            if (cmd.Action == "capture") {
                if (!Guid.TryParse(cmd.Key, out _)) throw new InvalidOperationException("outputs_changed");
            } else if (cmd.Action == "create") {
                if (cmd.ExpectedCount != count) throw new InvalidOperationException("outputs_changed");
                count++; mutations++;
            } else if (cmd.Action != "list") {
                if (cmd.Key != key) throw new InvalidOperationException("output_protected");
                enabled = cmd.Action == "enable"; mutations++;
            }
            return Task.FromResult(new DisplayCompanionServer.SessionDisplayState([
                new(captureKey, "Capture", true, false, true), new(key, "Secondary", enabled, true, false)], count, "available"));
        };
        var capableInventory = await Request(physicalOnly, "GET", "/v1/displays", null);
        if (capableInventory.Body["supportsSessionDisplays"]?.GetValue<bool>() != true)
            throw new Exception("Session controls were not advertised to mobile clients.");
        const string sessionPath = "/v1/displays/session";
        var targetDevice = Guid.NewGuid().ToString();
        if ((await Request(physicalOnly, "POST", sessionPath, Body("capture", targetDevice), false)).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("capture", targetDevice, id: Guid.NewGuid().ToString()))).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("capture", "bad-target"))).Code != 400 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("capture", targetDevice))).Code != 200)
            throw new Exception("Live capture must validate the paired session and target identifier.");
        if ((await Request(physicalOnly, "POST", sessionPath, Body("enable", key), false)).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("list", id: Guid.NewGuid().ToString()))).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("create", expected: -1))).Code != 400 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("disable", captureKey))).Code != 409 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("enable", "x"))).Code != 400 || mutations != 0)
            throw new Exception("Display controls accepted an invalid request.");
        var enabledResult = await Request(physicalOnly, "POST", sessionPath, Body("enable", key));
        if (enabledResult.Code != 200 || enabledResult.Body["outputs"]?[1]?["active"]?.GetValue<bool>() != true ||
            (await Request(physicalOnly, "POST", sessionPath, Body("disable", key))).Code != 200 || enabled ||
            (await Request(physicalOnly, "POST", sessionPath, Body("create", expected: 1))).Code != 200 ||
            (await Request(physicalOnly, "POST", sessionPath, Body("create", expected: 1))).Code != 409 ||
            (await Request(busy, "POST", sessionPath, Body("list"))).Code != 404 || mutations != 3)
            throw new Exception("Display state or create replay contract changed.");
        Console.WriteLine("PASS live outputs: paired session, enable, disable, protected capture and compare-and-set creation");
        var activeMode = new DisplayTopologyService.DisplayMode(1920, 1080, 60);
        var largerMode = new DisplayTopologyService.DisplayMode(2560, 1440, 60);
        var available = new[] { activeMode, largerMode };
        physicalOnly.SessionDisplays = (owner, cmd, _) => {
            if (owner != Convert.ToHexString(SHA256.HashData(ClientCertificate.RawData)) || cmd.SessionId != session)
                throw new UnauthorizedAccessException();
            if (cmd.Action is "resize" or "resize-stream") {
                if (cmd.Key != captureKey) throw new InvalidOperationException("outputs_changed");
                activeMode = available.SingleOrDefault(m => m.Width == cmd.Width && m.Height == cmd.Height)
                    ?? throw new InvalidOperationException("display_mode_unavailable");
            }
            return Task.FromResult(new DisplayCompanionServer.SessionDisplayState(
                [new(captureKey, "Virtual desktop", true, true, true)], null, "physical_capture_required",
                new(captureKey, activeMode, available), new(activeMode.Width, activeMode.Height)));
        };
        string Resize(int? w, int? h, string? output = null, string? id = null) =>
            System.Text.Json.JsonSerializer.Serialize(new { sessionId = id ?? session, action = "resize", key = output ?? captureKey, width = w, height = h });
        if ((await Request(physicalOnly, "POST", sessionPath, Resize(2560, 1440), false)).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, Resize(2560, 1440, id: Guid.NewGuid().ToString()))).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, Resize(2560, null))).Code != 400 ||
            (await Request(physicalOnly, "POST", sessionPath, Resize(-1, 1440))).Code != 400 ||
            (await Request(physicalOnly, "POST", sessionPath, Resize(2560, 1440, key))).Code != 409 ||
            (await Request(physicalOnly, "POST", sessionPath, Resize(1234, 789))).Body["error"]?.GetValue<string>() != "display_mode_unavailable")
            throw new Exception("Invalid live resize request was accepted.");
        foreach (var mode in new[] { largerMode, largerMode, available[0] }) {
            var result = await Request(physicalOnly, "POST", sessionPath, Resize(mode.Width, mode.Height));
            var current = result.Body["virtualDesktop"]?["current"];
            if (result.Code != 200 || current?["width"]?.GetValue<int>() != mode.Width || current["height"]?.GetValue<int>() != mode.Height)
                throw new Exception("Live resize did not return the applied dimensions.");
        }
        Console.WriteLine("PASS virtual desktop resize: paired session, stable target, supported mode, resize, replay and restore");
        var synchronizedResize = Resize(2560, 1440).Replace("\"resize\"", "\"resize-stream\"");
        var synchronizedResult = await Request(physicalOnly, "POST", sessionPath, synchronizedResize);
        if ((await Request(physicalOnly, "POST", sessionPath, synchronizedResize, false)).Code != 403 ||
            synchronizedResult.Code != 200 || synchronizedResult.Body["encodedVideo"]?["width"]?.GetValue<int>() != 2560 ||
            synchronizedResult.Body["virtualDesktop"]?["current"]?["height"]?.GetValue<int>() != 1440)
            throw new Exception("Synchronized resize must authenticate and report the encoder and desktop readbacks.");
        string StreamMode(int w, int fps, string? id = null, string? key = null) =>
            System.Text.Json.JsonSerializer.Serialize(new { sessionId = id ?? session, action = "stream-mode",
                key = key ?? captureKey, width = w, height = 1080, fps });
        physicalOnly.SessionDisplays = (owner, cmd, _) => {
            if (owner != Convert.ToHexString(SHA256.HashData(ClientCertificate.RawData)) || cmd.SessionId != session)
                throw new UnauthorizedAccessException();
            if (cmd.Key != captureKey) throw new InvalidOperationException("outputs_changed");
            return Task.FromResult(new DisplayCompanionServer.SessionDisplayState(
                [new(captureKey, "Monitor", true, false, true)], null, "", null,
                new(cmd.Width!.Value, cmd.Height!.Value, cmd.Fps!.Value)));
        };
        var live = await Request(physicalOnly, "POST", sessionPath, StreamMode(1920, 30));
        if (live.Code != 200 || live.Body["encodedVideo"]?["fps"]?.GetValue<int>() != 30 ||
            (await Request(physicalOnly, "POST", sessionPath, StreamMode(1920, 30), false)).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, StreamMode(1920, 30, Guid.NewGuid().ToString()))).Code != 403 ||
            (await Request(physicalOnly, "POST", sessionPath, StreamMode(1920, 30, key: new string('f',64)))).Code != 409 ||
            (await Request(physicalOnly, "POST", sessionPath, StreamMode(1921, 30))).Code != 400 ||
            (await Request(physicalOnly, "POST", sessionPath, StreamMode(1920, 0))).Code != 400 ||
            (await Request(physicalOnly, "POST", sessionPath, StreamMode(1920, 241))).Code != 400)
            throw new Exception("Live stream-mode ownership, bounds or acknowledgement changed.");
        Console.WriteLine("PASS live stream mode checks paired owner, session, capture key, dimensions and frame rate");
        const string inputPath = "/v1/displays/input-status";
        int probes = 0;
        physicalOnly.OwnsActiveSession = (peer, id, _) => Task.FromResult(
            peer == Convert.ToHexString(SHA256.HashData(ClientCertificate.RawData)) && id == session);
        physicalOnly.TextInputAtPointer = () => { probes++; return true; };
        physicalOnly.MicrophoneInUse = (_, _) => { probes++; return false; };
        foreach (var action in new[] { "viewportCaret", "viewportPointer" }) {
            if ((await Request(physicalOnly, "POST", inputPath, Body(action), false)).Code != 403 ||
                (await Request(physicalOnly, "POST", inputPath, Body(action, id: Guid.NewGuid().ToString()))).Code != 403)
                throw new Exception($"Viewport geometry requires an authenticated active session for {action}.");
            var geometry = await Request(physicalOnly, "POST", inputPath, Body(action));
            if (geometry.Code != 200 || !geometry.Body.ContainsKey("pointer") || !geometry.Body.ContainsKey("caret"))
                throw new Exception($"Viewport geometry response changed for {action}.");
        }
        if ((await Request(physicalOnly, "POST", inputPath, Body("textFocus"), false)).Code != 403 ||
            (await Request(physicalOnly, "POST", inputPath, Body("virtualMicrophoneUse", id: Guid.NewGuid().ToString()))).Code != 403 ||
            (await Request(physicalOnly, "POST", inputPath, Body("unknown"))).Code != 400 || probes != 0 ||
            (await Request(physicalOnly, "POST", inputPath, Body("textFocus"))).Body["active"]?.GetValue<bool>() != true ||
            (await Request(physicalOnly, "POST", inputPath, Body("virtualMicrophoneUse"))).Body["active"]?.GetValue<bool>() != false || probes != 2)
            throw new Exception("Input status must be read only by the current paired stream.");
        Console.WriteLine("PASS paired stream input hints");
        var contract = new JsonObject {
            ["inventory"] = inventory.Body,
            ["selectionRequests"] = new JsonArray(requests.Select(x => JsonNode.Parse(x)).ToArray()),
            ["ready"] = ready
        };
        if (export) {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            await File.WriteAllTextAsync(fixturePath, contract.ToJsonString(new() { WriteIndented = true }));
        } else if (!JsonNode.DeepEquals(contract, JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))))
            throw new Exception("Host changed the Android companion contract; review/update both ends");
        foreach (bool tls in new[] { false, true }) {
            var removed = await Request(server, "POST", "/v1/moonlight/pair", null, tls);
            if (removed.Code != 404) throw new Exception("Obsolete remote PIN approval route must remain unavailable.");
        }
        server.UpdateLease = (_, _, _, _) => throw new System.Net.Http.HttpRequestException("engine restarting");
        if ((await Request(server, "POST", "/v1/displays/lease", "{")).Code != 400 ||
            (await Request(server, "POST", "/v1/displays/lease", "{\"requestId\":\"abc\"}")).Code != 400 ||
            (await Request(server, "POST", "/v1/displays/lease", $"{{\"requestId\":\"{Guid.NewGuid()}\",\"action\":\"abort\"}}")).Code != 503)
            throw new Exception("Display lease endpoint must return structured HTTP error codes instead of dropping connections.");
        Console.WriteLine("PASS companion wire contract: actual HTTP inventory/selection, malformed input, 409, 503 and lease errors");
    }
    private static async Task<(int Code, JsonObject Body)> Request(DisplayCompanionServer server, string method, string path, string? body, bool secure = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            server.LoadTlsCertificate = () => new X509Certificate2(ServerCertificate.Export(X509ContentType.Pkcs12));
            server.AuthenticatePeer = peer => peer != null && peer.GetRawCertData().AsSpan().SequenceEqual(ClientCertificate.RawData);
            var handling = (Task)typeof(DisplayCompanionServer).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [accepted, timeout.Token])!;
            using var tls = secure ? new SslStream(client.GetStream(), true, (_, peer, _, _) =>
                peer != null && peer.GetRawCertData().AsSpan().SequenceEqual(ServerCertificate.RawData)) : null;
            try { if (tls != null) await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "localhost", ClientCertificates = new X509CertificateCollection { ClientCertificate },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token); } catch { await handling; throw; }
            Stream stream = tls is null ? client.GetStream() : tls;
            var payload = Encoding.UTF8.GetBytes(body ?? "");
            var header = Encoding.ASCII.GetBytes($"{method} {path} HTTP/1.1\r\nHost: localhost\r\nContent-Length: {payload.Length}\r\n\r\n");
            await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(payload, timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var response = await reader.ReadToEndAsync(timeout.Token); await handling;
            return (int.Parse(response.Split(' ')[1]), JsonNode.Parse(response[(response.IndexOf("\r\n\r\n") + 4)..])!.AsObject());
        } finally { listener.Stop(); }
    }
}
