using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Storage;
using PiuLink.Client.Core.Streaming;
using System.IO.Pipes;
using System.Text;

if (args.FirstOrDefault() == "stream" && Environment.GetEnvironmentVariable("PIULINK_TEST_CODEC_RECORD") is { } resultPath &&
    Environment.GetEnvironmentVariable("PIULINK_ENGINE_PIPE") is { } pipeName)
{
    await File.WriteAllTextAsync(resultPath, Environment.GetEnvironmentVariable("PIULINK_CODEC_POLICY") ?? "");
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(5000);
    await pipe.ReadAsync(new byte[1]);
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Matches Host companion contract and preserves desktop preferences", CompanionTests.Run),
    ("LAN discovery retains a valid Host alongside a malformed response", CompanionTests.DiscoverMixedResponses),
    ("Verifies HTTP XML status, quit, fast 409 failure, bounded discovery, and identity disposal", CompanionTests.VerifyBugfixContracts),
    ("Qt child receives the selected codec policy", TestQtCodecEnvironment),
    ("Parses DNS host with default port", ParseDnsHost),
    ("Parses host with explicit port", ParseHostPort),
    ("Parses bracketed IPv6", ParseIpv6),
    ("Rejects URL input", RejectUrl),
    ("Host invitation matches the directory protocol and rejects changed origins", TestHostInvitation),
    ("Recognizes Tailscale IPv4", RecognizeTailscaleIpv4),
    ("Recognizes Tailscale IPv6", RecognizeTailscaleIpv6),
    ("Recognizes Tailscale MagicDNS", RecognizeTailscaleDns),
    ("Builds safe pairing command", BuildPairCommand),
    ("Locates packaged Qt Moonlight engine", LocateQtMoonlightEngine),
    ("Rejects invalid pairing PIN", RejectInvalidPin),
    ("Builds stream preferences", BuildStreamCommand),
    ("Validates stream range", ValidateStreamRange),
    ("Parses Moonlight app list", ParseAppList),
    ("Persists hosts locally", PersistHosts),
    ("Rejects invalid writes without losing saved hosts", InvalidWritesPreserveHosts)
    ,("Reconnect policy distinguishes abnormal and user exits", ReconnectPolicy)
    ,("Serializes validated PiuLink engine commands", PiuLinkEngineCommands)
    ,("Parses complete PiuLink engine telemetry", PiuLinkEngineTelemetry)
    ,("Exchanges commands and telemetry over the engine pipe", PiuLinkEnginePipe)
    ,("Startup bitrate stays fixed while telemetry is collected", AutoBitrateControl)
    ,("Auto codec policy separates efficiency and latency routes", AutoCodecSelection)
    ,("Parses Host Wake-on-LAN and PiuLink capabilities", ParseHostCapabilities)
    ,("Builds a valid Wake-on-LAN magic packet", WakeOnLanPacket)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static Task ReconnectPolicy()
{
    True(StreamReconnectPolicy.ShouldReconnect(1, false, 0));
    True(StreamReconnectPolicy.ShouldReconnect(-1, false, 2));
    True(!StreamReconnectPolicy.ShouldReconnect(0, false, 0));
    True(!StreamReconnectPolicy.ShouldReconnect(1, true, 0));
    True(!StreamReconnectPolicy.ShouldReconnect(1, false, 3));
    Equal(TimeSpan.FromSeconds(2), StreamReconnectPolicy.DelayForAttempt(0));
    Equal(TimeSpan.FromSeconds(10), StreamReconnectPolicy.DelayForAttempt(2));
    return Task.CompletedTask;
}

static Task AutoBitrateControl()
{
    Equal(30000, AutoBitrateController.RecommendedStartupKbps(
        HostEndpoint.Parse("192.168.1.20"), "1920x1080", 60));
    Equal(25000, AutoBitrateController.RecommendedStartupKbps(
        HostEndpoint.Parse("100.100.20.30"), "3840x2160", 60));
    Equal(15000, AutoBitrateController.RecommendedStartupKbps(
        HostEndpoint.Parse("203.0.113.10"), "3840x2160", 60));
    var automatic = new AutoBitrateController(30000);
    var healthy = Telemetry(loss: 0, jitter: 0, rtt: 15, variance: 2) with { ReceivedMbps = 19 };
    automatic.Observe(healthy, 0);
    automatic.Observe(healthy, 1000);
    automatic.Observe(healthy, 2000);
    Equal(30000, automatic.TargetKbps);
    Equal(19000, automatic.HistoricalThroughputKbps);
    var congested = Telemetry(loss: 3, jitter: 1, rtt: 30, variance: 2);
    for (var i = 3; i < 20; i++) automatic.Observe(congested, i * 1000L);
    Equal(30000, automatic.TargetKbps);
    return Task.CompletedTask;
}

static async Task TestQtCodecEnvironment()
{
    var result = Path.Combine(Path.GetTempPath(), "piulink-codec-" + Guid.NewGuid().ToString("N"));
    var previous = Environment.GetEnvironmentVariable("PIULINK_TEST_CODEC_RECORD");
    Environment.SetEnvironmentVariable("PIULINK_TEST_CODEC_RECORD", result);
    try
    {
        var engine = new MoonlightQtCommandService(Path.Combine(AppContext.BaseDirectory,
            "PiuLink.Client.Core.Tests" + (OperatingSystem.IsWindows() ? ".exe" : "")));
        var profile = StreamProfile.Default with { Resolution = "1920x1080", VideoCodec = "auto" };
        foreach (var (host, settings, expected) in new[] { ("100.64.0.10", profile, "efficiency"),
                 ("192.168.1.10", profile, "balanced"), ("100.64.0.10", profile with { VideoCodec = "H.264" }, "balanced") })
        {
            await using var child = await engine.LaunchStreamAsync(HostEndpoint.Parse(host), "Desktop", settings);
            Equal(expected, await File.ReadAllTextAsync(result));
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("PIULINK_TEST_CODEC_RECORD", previous);
        File.Delete(result);
    }
}

static Task AutoCodecSelection()
{
    var local = HostEndpoint.Parse("192.168.1.20");
    var tailscale = HostEndpoint.Parse("100.100.20.30");
    var fullHd = new StreamProfile("1920x1080", 60, 20000, "fullscreen", "auto", false, true);
    var fourK = fullHd with { Resolution = "3840x2160", BitrateKbps = 60000 };
    Equal(AutoCodecMode.Balanced, AutoCodecPolicy.Decide(local, fullHd).Mode);
    Equal("auto:local-low-latency", AutoCodecPolicy.Decide(local, fullHd).Reason);
    Equal(AutoCodecMode.Efficiency, AutoCodecPolicy.Decide(tailscale, fullHd).Mode);
    Equal("auto:tailscale", AutoCodecPolicy.Decide(tailscale, fullHd).Reason);
    Equal(AutoCodecMode.Efficiency, AutoCodecPolicy.Decide(local, fourK).Mode);
    Equal("auto:high-resolution", AutoCodecPolicy.Decide(local, fourK).Reason);
    Equal("manual:H.264", AutoCodecPolicy.Decide(local, fullHd with { VideoCodec = "H.264" }).Reason);
    return Task.CompletedTask;
}

static Task ParseHostCapabilities()
{
    const string xml = "<root><appversion>7.1.431.-1</appversion><HttpsPort>47984</HttpsPort><ServerCodecModeSupport>257</ServerCodecModeSupport><currentgame>0</currentgame><PairStatus>1</PairStatus><mac>00:11:22:AA:BB:CC</mac><piulink_control>1</piulink_control></root>";
    var info = GameStreamHttpClient.ParseServerInfo(xml);
    Equal("00:11:22:AA:BB:CC", info.MacAddress);
    True(info.SupportsPiuLinkControl);
    return Task.CompletedTask;
}

static Task WakeOnLanPacket()
{
    var packet = WakeOnLanService.CreateMagicPacket("00:11:22:AA:BB:CC");
    Equal(102, packet.Length);
    True(packet.Take(6).All(value => value == 0xFF));
    var mac = Convert.FromHexString("001122AABBCC");
    for (var offset = 6; offset < packet.Length; offset += 6)
        True(packet.AsSpan(offset, 6).SequenceEqual(mac));
    Throws<FormatException>(() => WakeOnLanService.CreateMagicPacket("not-a-mac"));
    return Task.CompletedTask;
}

static PiuLinkEngineTelemetry Telemetry(double loss, double jitter, int rtt, int variance) =>
    new(1, 1, "streaming", "HEVC", 80000, 80000, 80000, 60,
        true, true, "active-full", 60, loss, jitter, rtt, variance, 1, 1);

static Task ParseDnsHost()
{
    var endpoint = HostEndpoint.Parse("gaming-pc.tail1234.ts.net");
    Equal("gaming-pc.tail1234.ts.net", endpoint.Host);
    Equal(47989, endpoint.Port);
    Equal("gaming-pc.tail1234.ts.net", endpoint.MoonlightTarget);
    return Task.CompletedTask;
}

static Task ParseHostPort()
{
    var endpoint = HostEndpoint.Parse("192.168.1.50:48000");
    Equal("192.168.1.50", endpoint.Host);
    Equal(48000, endpoint.Port);
    Equal("192.168.1.50:48000", endpoint.MoonlightTarget);
    return Task.CompletedTask;
}

static Task ParseIpv6()
{
    var endpoint = HostEndpoint.Parse("[fd00::20]:48000");
    Equal("fd00::20", endpoint.Host);
    Equal(48000, endpoint.Port);
    Equal("[fd00::20]:48000", endpoint.MoonlightTarget);
    Equal("[fd00::20]", HostEndpoint.Parse("fd00::20").MoonlightTarget);
    Throws<FormatException>(() => HostEndpoint.Parse("[example.com]:48000"));
    Throws<FormatException>(() => HostEndpoint.Parse("--help"));
    return Task.CompletedTask;
}

static Task RejectUrl()
{
    Throws<FormatException>(() => HostEndpoint.Parse("https://192.168.1.50"));
    return Task.CompletedTask;
}

static async Task TestHostInvitation()
{
    var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    var invitation = PiuLink.Rendezvous.HostInvite.FromOwner(new string('a', 64), id);
    Equal("459617d8c4ee175243e93401e6f519aac468220ae2e04d14fae2457320c2ff53", invitation.Room);
    True(PiuLink.Rendezvous.HostInvite.Parse(invitation.Link) == invitation);
    Throws<FormatException>(() => PiuLink.Rendezvous.HostInvite.Parse(invitation.Link.Replace("atipiu.org", "example.test")));
    Throws<FormatException>(() => PiuLink.Rendezvous.HostInvite.Parse(invitation.Link[..^1]));
    True(!PiuLink.Rendezvous.HostInvite.IsCandidate(System.Net.IPAddress.Parse("::ffff:127.0.0.1")));
    True(!PiuLink.Rendezvous.HostInvite.IsCandidate(System.Net.IPAddress.Parse("169.254.1.1")));
    Equal("[fd00::20]:48000", (await HostEndpoint.ResolveAsync("[fd00::20]:48000")).MoonlightTarget);
}

static Task RecognizeTailscaleIpv4()
{
    True(HostEndpoint.Parse("100.100.20.30").IsTailscaleAddress);
    True(!HostEndpoint.Parse("192.168.50.30").IsTailscaleAddress);
    return Task.CompletedTask;
}

static Task RecognizeTailscaleIpv6()
{
    True(HostEndpoint.Parse("fd7a:115c:a1e0::2d3b:667f").IsTailscaleAddress);
    True(!HostEndpoint.Parse("fd00::20").IsTailscaleAddress);
    return Task.CompletedTask;
}

static Task RecognizeTailscaleDns()
{
    True(HostEndpoint.Parse("gaming-pc.tail1234.ts.net").IsTailscaleAddress);
    return Task.CompletedTask;
}

static Task BuildPairCommand()
{
    var command = MoonlightCommandBuilder.Pair(HostEndpoint.Parse("pc.local"), "0427");
    SequenceEqual(["pair", "--pin", "0427", "pc.local"], command);
    return Task.CompletedTask;
}

static Task LocateQtMoonlightEngine()
{
    var original = Environment.GetEnvironmentVariable("PIULINK_MOONLIGHT_QT_EXE_PATH");
    var directory = Path.Combine(Path.GetTempPath(), $"piulink-qt-engine-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var executable = Path.Combine(directory, "PiuLink-Moonlight.exe");
    try
    {
        File.WriteAllBytes(executable, []);
        Environment.SetEnvironmentVariable("PIULINK_MOONLIGHT_QT_EXE_PATH", executable);
        var location = new MoonlightQtEngineLocator().Locate();
        True(location.IsAvailable);
        Equal(Path.GetFullPath(executable), location.ExecutablePath);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PIULINK_MOONLIGHT_QT_EXE_PATH", original);
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

static Task RejectInvalidPin()
{
    Throws<ArgumentException>(() => MoonlightCommandBuilder.Pair(HostEndpoint.Parse("pc.local"), "12ab"));
    return Task.CompletedTask;
}

static Task BuildStreamCommand()
{
    var profile = new StreamProfile("2560x1440", 120, 50000, "borderless", "HEVC", true, false);
    var command = MoonlightCommandBuilder.Stream(HostEndpoint.Parse("100.80.1.2"), "Desktop", profile);
    True(command.Contains("--resolution"));
    True(command.Contains("2560x1440"));
    True(command.Contains("--hdr"));
    True(command.Contains("--no-absolute-mouse"));
    True(command.Contains("--game-optimization"));
    True(command.Contains("--gamepad-deadzone"));
    True(command.Contains("7"));
    Equal("Desktop", command[^1]);

    var customized = profile with
    {
        ShowPerformanceOverlay = false,
        EnableVsync = false,
        FramePacing = true,
        AudioConfiguration = "5.1-surround",
        MultiController = false,
        PlayAudioOnHost = true,
        MuteOnFocusLoss = true,
        BackgroundGamepad = true,
        SwapGamepadFaceButtons = true,
        EnableYuv444 = true,
        CaptureSystemKeys = "always",
        GamepadDeadzonePercent = 15
    };
    var customizedCommand = MoonlightCommandBuilder.Stream(HostEndpoint.Parse("pc.local"), "Desktop", customized);
    var external = MoonlightCommandBuilder.Stream(HostEndpoint.Parse("pc.local"), "Desktop", customized, integratedEngine: false);
    True(!external.Contains("--gamepad-deadzone"));
    True(external.Contains("--no-performance-overlay"));
    Equal("Desktop", external[^1]);
    foreach (var argument in new[]
    {
        "--no-performance-overlay", "--no-vsync", "--frame-pacing", "--audio-config", "5.1-surround",
        "--no-multi-controller", "--audio-on-host", "--mute-on-focus-loss", "--background-gamepad",
        "--swap-gamepad-buttons", "--yuv444", "--capture-system-keys", "always", "--gamepad-deadzone", "15"
    })
    {
        True(customizedCommand.Contains(argument));
    }
    Throws<ArgumentException>(() => MoonlightCommandBuilder.Stream(
        HostEndpoint.Parse("pc.local"), "  --help", profile));
    return Task.CompletedTask;
}

static Task ValidateStreamRange()
{
    Throws<ArgumentOutOfRangeException>(() =>
        new StreamProfile("1920x1080", 5, 20000, "fullscreen", "auto", false, true).Validate());
    return Task.CompletedTask;
}

static Task ParseAppList()
{
    var apps = MoonlightProcessService.ParseAppList("Desktop\r\nSteam Big Picture\r\nDesktop\r\n");
    SequenceEqual(["Desktop", "Steam Big Picture"], apps);
    return Task.CompletedTask;
}

static async Task PersistHosts()
{
    var directory = Path.Combine(Path.GetTempPath(), $"piulink-client-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var repository = new JsonHostRepository(Path.Combine(directory, "hosts.json"));
        var expected = new HostProfile(Guid.NewGuid(), "Gaming PC", "192.168.1.50", DateTimeOffset.UtcNow);
        await repository.SaveAsync([expected]);
        var actual = await repository.LoadAsync();
        Equal(1, actual.Count);
        Equal(expected, actual[0]);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task InvalidWritesPreserveHosts()
{
    var directory = Path.Combine(Path.GetTempPath(), $"piulink-storage-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "hosts.json");
        var repository = new JsonHostRepository(file);
        var original = new HostProfile(Guid.NewGuid(), "PC", "pc.local", DateTimeOffset.UtcNow);
        await repository.SaveAsync([original]);
        var before = await File.ReadAllTextAsync(file);
        try { await repository.SaveAsync([original, original]); throw new Exception("Duplicate ID accepted"); }
        catch (InvalidDataException) { }
        Equal(before, await File.ReadAllTextAsync(file));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await repository.SaveAsync([], cancellation.Token); throw new Exception("Cancelled write committed"); }
        catch (OperationCanceledException) { }
        Equal(before, await File.ReadAllTextAsync(file));
        await File.WriteAllTextAsync(file, "null");
        try { await repository.LoadAsync(); throw new Exception("Null document accepted as an empty list"); }
        catch (InvalidDataException) { }
    }
    finally { Directory.Delete(directory, true); }
}

static Task PiuLinkEngineCommands()
{
    var bitrate = PiuLinkEngineProtocol.SerializeCommand(PiuLinkEngineCommand.SetBitrate(45000));
    True(bitrate.Contains("\"command\":\"setBitrateKbps\"", StringComparison.Ordinal));
    True(bitrate.Contains("\"value\":45000", StringComparison.Ordinal));
    var adaptive = PiuLinkEngineProtocol.SerializeCommand(PiuLinkEngineCommand.SetContentAdaptive(true));
    True(adaptive.Contains("\"command\":\"setContentAdaptive\"", StringComparison.Ordinal));
    var gamepad = PiuLinkEngineProtocol.SerializeCommand(PiuLinkEngineCommand.SetVirtualGamepad(0x9001, -32767, 32767, 0, 1, 255, 0));
    True(gamepad.Contains("\"command\":\"setVirtualGamepad\"", StringComparison.Ordinal));
    True(gamepad.Contains("\"leftTrigger\":255", StringComparison.Ordinal));
    Throws<InvalidDataException>(() => PiuLinkEngineProtocol.SerializeCommand(
        new PiuLinkEngineCommand(PiuLinkEngineProtocol.CurrentVersion, "setBitrateKbps", 300)));
    Throws<InvalidDataException>(() => PiuLinkEngineProtocol.SerializeCommand(
        PiuLinkEngineCommand.SetVirtualGamepad(0, 0, 0, 0, 0, 256, 0)));
    Throws<ArgumentException>(() => (StreamProfile.Default with { ColorRange = "invalid" }).Validate());
    return Task.CompletedTask;
}

static Task PiuLinkEngineTelemetry()
{
    const string json = """
        {"protocolVersion":1,"sequence":7,"state":"streaming","codec":"HEVC 10-bit HDR","negotiatedCeilingKbps":80000,"autoTargetKbps":42000,"engineTargetKbps":25200,"receivedMbps":23.8,"contentAdaptiveRequested":true,"contentAdaptiveConfirmed":true,"contentAdaptiveState":"static-reduced","framesPerSecond":59.94,"networkDroppedPercent":0.12,"jitterDroppedPercent":0.03,"roundTripTimeMs":18,"roundTripVarianceMs":3,"decodeTimeMs":1.7,"renderTimeMs":2.1}
        """;
    var telemetry = PiuLinkEngineProtocol.ParseTelemetry(json);
    Equal(25200, telemetry.EngineTargetKbps);
    True(telemetry.ContentAdaptiveConfirmed);
    Equal("static-reduced", telemetry.ContentAdaptiveState);
    True(Math.Abs(telemetry.ReceivedMbps - 23.8) < 0.001);
    Throws<InvalidDataException>(() => PiuLinkEngineProtocol.ParseTelemetry(
        json.Replace("\"protocolVersion\":1", "\"protocolVersion\":2", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static async Task PiuLinkEnginePipe()
{
    await using var server = new PiuLinkEngineChannel();
    using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    var connect = client.ConnectAsync(2000);
    await server.WaitForConnectionAsync(TimeSpan.FromSeconds(2));
    await connect;
    using var reader = new StreamReader(client, new UTF8Encoding(false), false, leaveOpen: true);
    using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

    var readCommand = reader.ReadLineAsync();
    await server.SendAsync(PiuLinkEngineCommand.SetContentAdaptive(true)).WaitAsync(TimeSpan.FromSeconds(2));
    var command = await readCommand.WaitAsync(TimeSpan.FromSeconds(2));
    True(command?.Contains("setContentAdaptive", StringComparison.Ordinal) == true);

    const string telemetry = """
        {"protocolVersion":1,"sequence":1,"state":"streaming","codec":"AV1","negotiatedCeilingKbps":80000,"autoTargetKbps":50000,"engineTargetKbps":50000,"receivedMbps":48.2,"contentAdaptiveRequested":true,"contentAdaptiveConfirmed":true,"contentAdaptiveState":"active-full","framesPerSecond":120,"networkDroppedPercent":0,"jitterDroppedPercent":0,"roundTripTimeMs":9,"roundTripVarianceMs":1,"decodeTimeMs":0.8,"renderTimeMs":1.2}
        """;
    await using var samples = server.ReadTelemetryAsync().GetAsyncEnumerator();
    var readTelemetry = samples.MoveNextAsync().AsTask();
    await writer.WriteLineAsync(telemetry).WaitAsync(TimeSpan.FromSeconds(2));
    True(await readTelemetry.WaitAsync(TimeSpan.FromSeconds(2)));
    Equal("AV1", samples.Current.Codec);
    Equal(50000, samples.Current.EngineTargetKbps);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void True(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Expected condition to be true.");
    }
}

static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException("Sequences differ.");
    }
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
