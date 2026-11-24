using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Storage;
using PiuLink.Client.Core.Streaming;

const string help = """
    PiuLink Client CLI for Windows

    piulink hosts [--json]                   List saved Host profiles
    piulink discover [--json]                Find Hosts on the local network
    piulink probe <host> [--json]            Check whether the streaming port responds
    piulink pair <host>                      Generate a PIN for local approval on the Host
    piulink apps <host> [--json]             Read the paired Host's application list
    piulink displays <host> [--json]         Read the paired Host's available displays
    piulink stream <host> <app> [options]    Open a stream and wait for the window to close
    piulink quit <host>                      Close the current Host application
    piulink wake <host> [mac]                Send a Wake-on-LAN packet

    <host> accepts a saved name, profile ID, network address, or quoted Host invitation.
    Stream options override the saved profile for this run:
      --resolution WIDTHxHEIGHT   --fps NUMBER   --bitrate KBPS   --display-id ID
    """;

Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
var token = cancellation.Token;
var repository = new JsonHostRepository();
var json = args.Length > 0 && args[^1] == "--json";
var command = json ? args[..^1] : args;

try
{
    switch (command)
    {
        case [] or ["-h" or "--help" or "help"] when !json:
            Console.WriteLine(help);
            return 0;
        case ["hosts"]:
        {
            var hosts = await repository.LoadAsync(token);
            if (json) PrintJson(hosts.Select(h => new { h.Id, h.Name, h.Address, h.CreatedAt, h.LastConnectedAt,
                h.StreamSettings, h.DisplayId, h.RecentApplications, h.MacAddress }));
            else PrintList(hosts, host => $"{host.Name}\t{host.Address}\t{host.Id}", "No saved Hosts.");
            return 0;
        }
        case ["discover"]:
        {
            var hosts = await new LanHostDiscoveryService().DiscoverAsync(token);
            PrintList(hosts, host => $"{host.Name}\t{host.Address}", "No Hosts discovered.");
            return 0;
        }
        case ["probe", var target]:
        {
            var (endpoint, _) = await ResolveHostAsync(target);
            var result = await new HostProbeService().ProbeAsync(endpoint, TimeSpan.FromSeconds(3), token);
            if (json) PrintJson(result);
            else Console.WriteLine(result.Detail);
            return result.IsReachable ? 0 : 2;
        }
        case ["pair", var target] when !json:
        {
            var (endpoint, _) = await ResolveHostAsync(target);
            var engine = GetEngine();
            var pin = RandomNumberGenerator.GetInt32(10000).ToString("D4", CultureInfo.InvariantCulture);
            Console.Error.WriteLine($"Enter PIN {pin} in PiuLink Host on {endpoint.MoonlightTarget}.");
            EnsureSuccess(await engine.PairAsync(endpoint, pin, TimeSpan.FromSeconds(65), token), "Pairing");
            Console.WriteLine("Pairing completed.");
            return 0;
        }
        case ["apps", var target]:
        {
            var (endpoint, _) = await ResolveHostAsync(target);
            var result = await GetEngine().ListAppsAsync(endpoint, TimeSpan.FromSeconds(45), token);
            EnsureSuccess(result, "Reading applications");
            PrintList(MoonlightProcessService.ParseAppList(result.StandardOutput), name => name, "No applications published.");
            return 0;
        }
        case ["displays", var target]:
        {
            var (endpoint, _) = await ResolveHostAsync(target);
            using var companion = new HostCompanionClient();
            var inventory = await companion.GetInventoryAsync(endpoint, token);
            if (json) PrintJson(inventory);
            else foreach (var display in inventory.Displays)
                Console.WriteLine($"{display.Name}\t{(display.IsVirtual ? "virtual" : "physical")}\t{display.DeviceId}");
            return 0;
        }
        case ["stream", var target, var app, .. var options] when !json:
        {
            var (endpoint, saved) = await ResolveHostAsync(target);
            var (profile, displayId) = ApplyStreamOptions(saved, options);
            return await StreamAsync(endpoint, app, profile, displayId);
        }
        case ["quit", var target] when !json:
        {
            var (endpoint, _) = await ResolveHostAsync(target);
            EnsureSuccess(await GetEngine().QuitAsync(endpoint, TimeSpan.FromSeconds(35), token), "Closing the application");
            Console.WriteLine("Host application closed.");
            return 0;
        }
        case ["wake", _] or ["wake", _, _] when !json:
        {
            var (endpoint, saved) = await ResolveHostAsync(command[1]);
            var mac = command.Length == 3 ? command[2] : saved?.MacAddress;
            if (string.IsNullOrWhiteSpace(mac))
                throw new ArgumentException("Supply a MAC address or choose a saved Host with a known MAC address.");
            await new WakeOnLanService().SendAsync(endpoint, mac, token);
            Console.WriteLine("Wake-on-LAN packet sent.");
            return 0;
        }
        default:
            Console.Error.WriteLine("Invalid command or arguments. Run piulink --help.");
            return 64;
    }
}
catch (OperationCanceledException) when (token.IsCancellationRequested)
{
    return 130;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The PiuLink operation timed out.");
    return 1;
}
catch (Exception error) when (error is ArgumentException or FormatException)
{
    Console.Error.WriteLine(error.Message);
    return 64;
}
catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or SocketException or Win32Exception
    or UnauthorizedAccessException or InvalidOperationException or JsonException or TimeoutException
    or CryptographicException or System.Xml.XmlException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

async Task<(HostEndpoint Endpoint, HostProfile? Saved)> ResolveHostAsync(string target)
{
    if (string.IsNullOrWhiteSpace(target) || target.StartsWith('-'))
        throw new ArgumentException("Choose a saved Host or supply a network address.");

    var hosts = await repository.LoadAsync(token);
    HostProfile? saved;
    if (Guid.TryParse(target, out var id))
    {
        saved = hosts.FirstOrDefault(host => host.Id == id)
            ?? throw new ArgumentException("No saved Host has that profile ID.");
    }
    else
    {
        var matches = hosts.Where(host => string.Equals(host.Name, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host.Address, target, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1)
            throw new ArgumentException("Multiple saved Hosts match. Run piulink hosts and use the profile ID.");
        saved = matches.SingleOrDefault();
    }

    return (await HostEndpoint.ResolveAsync(saved?.InviteLink ?? saved?.Address ?? target, saved?.Address, token), saved);
}

void PrintList<T>(IReadOnlyList<T> items, Func<T, string> display, string emptyText)
{
    if (json) PrintJson(items);
    else if (items.Count == 0) Console.WriteLine(emptyText);
    else foreach (var item in items) Console.WriteLine(display(item));
}

static void PrintJson<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

static MoonlightQtCommandService GetEngine()
{
    var location = new MoonlightQtEngineLocator().Locate();
    return new MoonlightQtCommandService(location.ExecutablePath ?? throw new IOException(location.Detail));
}

static void EnsureSuccess(MoonlightCommandResult result, string operation)
{
    if (result.IsSuccess) return;

    var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
    throw new IOException($"{operation} failed ({result.ExitCode}). {detail.Trim()}".TrimEnd());
}

static (StreamProfile Profile, string? DisplayId) ApplyStreamOptions(HostProfile? saved, string[] options)
{
    var profile = saved?.StreamSettings ?? StreamProfile.Default;
    var displayId = saved?.DisplayId;
    if (options.Length % 2 != 0)
        throw new ArgumentException("Every stream option requires a value. Run piulink --help.");

    for (var i = 0; i < options.Length; i += 2)
    {
        var value = options[i + 1];
        switch (options[i])
        {
            case "--resolution":
                profile = profile with { Resolution = value };
                break;
            case "--fps":
                profile = profile with { FramesPerSecond = ParseNumber(value, "--fps") };
                break;
            case "--bitrate":
                profile = profile with { BitrateKbps = ParseNumber(value, "--bitrate"), AutoBitrate = false };
                break;
            case "--display-id" when !string.IsNullOrWhiteSpace(value) && !value.StartsWith('-'):
                displayId = value;
                break;
            default:
                throw new ArgumentException($"Invalid stream option or value: {options[i]}. Run piulink --help.");
        }
    }

    profile.Validate();
    return (profile, displayId);
}

static int ParseNumber(string value, string option) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        ? number : throw new ArgumentException($"{option} requires a whole number.");

async Task<int> StreamAsync(HostEndpoint endpoint, string app, StreamProfile profile, string? displayId)
{
    if (string.IsNullOrWhiteSpace(app) || app.TrimStart().StartsWith('-'))
        throw new ArgumentException("Supply an application name from piulink apps <host>.");

    var engine = GetEngine();
    using var companion = new HostCompanionClient();
    var inventory = await companion.GetInventoryAsync(endpoint, token);
    var display = HostCompanionClient.ResolveStreamDisplay(inventory, displayId, profile);
    var launchProfile = profile with
    {
        BitrateKbps = profile.AutoBitrate
            ? AutoBitrateController.RecommendedStartupKbps(endpoint, profile.Resolution, profile.FramesPerSecond)
            : profile.BitrateKbps,
        ContentAdaptive = false
    };

    try
    {
        await companion.PrepareAsync(endpoint, display, profile, token);
        await using var session = await engine.LaunchStreamAsync(endpoint, app, launchProfile, token);
        using var telemetryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = DrainTelemetryAsync(session.Channel, telemetryCancellation.Token);
        try
        {
            Console.Error.WriteLine($"Streaming {app} from {endpoint.MoonlightTarget}. Close the window or press Ctrl+C to disconnect.");
            await session.WaitForExitAsync(token);
            token.ThrowIfCancellationRequested();
            if (session.ExitCode != 0)
                throw new IOException($"The streaming engine exited with code {session.ExitCode}.");
            return 0;
        }
        finally
        {
            telemetryCancellation.Cancel();
            await telemetry;
        }
    }
    finally
    {
        try { await companion.AbortAsync(endpoint); }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or CryptographicException
            or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"Could not release the Host display request: {error.Message}");
        }
    }
}

static async Task DrainTelemetryAsync(PiuLinkEngineChannel channel, CancellationToken token)
{
    try
    {
        await foreach (var _ in channel.ReadTelemetryAsync(token)) { }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException)
    {
        Console.Error.WriteLine($"Engine telemetry unavailable: {error.Message}");
    }
}
