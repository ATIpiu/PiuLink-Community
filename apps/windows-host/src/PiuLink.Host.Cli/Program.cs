using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiuLink.Host.Core.Diagnostics;
using PiuLink.Host.Core.Sunshine;
using PiuLink.Host.Services;

const string help = """
    PiuLink Host CLI for Windows

    piulink-host doctor [--json]    Check the engine, network, firewall and GPU
    piulink-host status [--json]    Show pairing and streaming status
    piulink-host pair <name>       Approve the client's four-digit PIN on this PC
    piulink-host end-stream        Close the current Host application
    piulink-host repair-desktop    Run the Host's display recovery

    Run with the Windows account that configured PiuLink Host.
    """;

Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
var token = cancellation.Token;
var readingPin = false;
Console.CancelKeyPress += (_, e) =>
{
    // An interrupted PIN prompt has no resources or pending operations to clean up.
    if (Volatile.Read(ref readingPin)) return;
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    for (var i = 0; i < args.Length; i++)
        if (string.Equals(args[i], "--json", StringComparison.OrdinalIgnoreCase)) args[i] = "--json";
    switch (args)
    {
        case ["-h" or "--help" or "help"]:
            Console.WriteLine(help);
            return 0;
        case [] or ["doctor"] or ["doctor", "--json"] or ["--json"]:
        {
            var snapshot = await HostDiagnosticsService.CreateDefault().CheckAsync(token);
            if (args.Contains("--json")) PrintJson(snapshot);
            else Console.Write(HostDiagnosticsService.FormatReport(snapshot));
            return snapshot.IsReady ? 0 : 2;
        }
        case ["--stream-state"]:
        case ["status"] or ["status", "--json"]:
        {
            var credential = ReadCredential();
            using var api = SunshineApiClient.CreateForLoopback();
            var overview = await api.GetOverviewAsync(credential.Username, credential.Secret, token);
            if (args[0] == "--stream-state")
                Console.WriteLine(JsonSerializer.Serialize(new { overview.HasActiveStreams, overview.HasRetainedApplication }));
            else if (args.Contains("--json"))
                PrintJson(overview);
            else
            {
                Console.WriteLine($"Published applications: {overview.ApplicationCount}");
                Console.WriteLine($"Paired clients: {overview.PairedClientCount}");
                Console.WriteLine($"Active streams: {(overview.ActiveStreamCount < 0 ? "unknown" : overview.ActiveStreamCount)}");
                Console.WriteLine($"Current application ID: {overview.CurrentApplicationId}");
                Console.WriteLine($"Retained application: {overview.HasRetainedApplication}");
            }
            return 0;
        }
        case ["pair", var name] when !string.IsNullOrWhiteSpace(name) && !name.StartsWith('-'):
        {
            var credential = ReadCredential();
            if (!Console.IsInputRedirected) Console.Error.Write("Enter the four-digit PIN shown by the client: ");
            Volatile.Write(ref readingPin, true);
            var pin = Console.ReadLine()?.Trim();
            Volatile.Write(ref readingPin, false);
            if (!SunshineApiClient.IsValidPairingPin(pin))
            {
                Console.Error.WriteLine("The pairing PIN must contain exactly four digits.");
                return 64;
            }

            using var api = SunshineApiClient.CreateForLoopback();
            await api.SubmitPinAsync(pin!, name, credential.Username, credential.Secret, token);
            Console.WriteLine("PIN submitted. Check the client for the pairing result.");
            return 0;
        }
        case ["end-stream"] or ["--end-stream"]:
        {
            var credential = ReadCredential();
            using var api = SunshineApiClient.CreateForLoopback();
            await api.CloseCurrentApplicationAsync(credential.Username, credential.Secret, token);
            Console.WriteLine("Sunshine streaming application closed.");
            return 0;
        }
        case ["repair-desktop"] or ["--restore-physical-desktop"]:
            return await RepairDesktopAsync();
        case ["--require-pairing-pin"]:
        {
            var credential = ReadCredential();
            using var api = SunshineApiClient.CreateForLoopback();
            var state = await api.GetOverviewAsync(credential.Username, credential.Secret, token);
            if (state.HasActiveStreams || state.HasCurrentApplication)
                throw new InvalidOperationException("End the current stream or retained application before applying engine settings.");

            await api.ApplyTrustedPairingSettingsAsync(false, false, credential.Username, credential.Secret, token);
            HostPreferences.AutoPairLan = false;
            HostPreferences.AutoPairTailnet = false;
            Console.WriteLine("First-time pairing requires a local PIN on LAN and Tailnet.");
            return 0;
        }
        case ["--enable-auto-pair"]:
            Console.Error.WriteLine("To pair a client, enter its PIN in PiuLink Host or run piulink-host pair <name>.");
            return 2;
        default:
            Console.Error.WriteLine("Invalid command or arguments. Run piulink-host --help.");
            return 64;
    }
}
catch (OperationCanceledException) when (token.IsCancellationRequested)
{
    return 130;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("PiuLink Host operation timed out.");
    return 1;
}
catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or Win32Exception
    or InvalidOperationException or JsonException or KeyNotFoundException or FormatException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static SunshineDeviceCredential ReadCredential() =>
    new SunshineManagedIdentity().TryRead()
    ?? throw new InvalidOperationException("Set up secure Sunshine control in PiuLink Host using this Windows account first.");

static void PrintJson<T>(T value)
{
    var options = new JsonSerializerOptions { WriteIndented = true };
    options.Converters.Add(new JsonStringEnumConverter());
    Console.WriteLine(JsonSerializer.Serialize(value, options));
}

static async Task<int> RepairDesktopAsync()
{
    var executable = Path.Combine(AppContext.BaseDirectory, "PiuLink.Host.exe");
    if (!File.Exists(executable))
    {
        executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ATIPiu Labs", "PiuLink Host", "PiuLink.Host.exe");
    }
    if (!File.Exists(executable) || !Version.TryParse(FileVersionInfo.GetVersionInfo(executable).FileVersion, out var installed)
        || installed < new Version(0, 2, 102))
        throw new InvalidOperationException("Desktop recovery requires PiuLink Host 0.2.102 or later.");

    // Let the Host finish its recovery transaction if the console is interrupted.
    using var helper = Process.Start(new ProcessStartInfo(executable, "--repair-desktop") { UseShellExecute = false })
        ?? throw new IOException("Could not start PiuLink Host desktop recovery.");
    await helper.WaitForExitAsync();
    if (helper.ExitCode == 0) Console.WriteLine("Desktop recovery complete.");
    else Console.Error.WriteLine("Desktop recovery did not complete. Check the display settings in PiuLink Host.");
    return helper.ExitCode;
}
