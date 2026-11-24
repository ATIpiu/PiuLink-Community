using System.Text.Json;
using PiuLink.Host.Core.Sunshine;
using PiuLink.Host.Services;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Models;

if (args.Length < 1) throw new ArgumentException("Specify the current Host address. Default mode only reads state.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
var credential = new WindowsCredentialStore().Read("ATIPiu Labs/PiuLink Host/Sunshine control") ?? throw new IOException("Control identity missing");
using var api = SunshineApiClient.CreateForLoopback();
var overview = await api.GetOverviewAsync(credential.Username, credential.Secret, timeout.Token);
var state = await api.GetDisplayStateAsync(credential.Username, credential.Secret, timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(new { overview.ActiveStreamCount, overview.CurrentApplicationId, state.Displays,
    Native = DisplayTopologyService.ReadDesktopState().Layout,
    Modes = state.Displays.Select(d => new { d.DeviceId, Source = DisplayTopologyService.ResolveDisplaySource(d.DisplayName, d.FriendlyName),
        ModeCount = DisplayTopologyService.GetDisplayModes(DisplayTopologyService.ResolveDisplaySource(d.DisplayName, d.FriendlyName)).Count }) }));
if (args.Contains("--engine-only")) return;
using var companion = new HostCompanionClient();
var endpoint = HostEndpoint.Parse(args[0]);
var inventory = await companion.GetInventoryAsync(endpoint, timeout.Token);
Console.WriteLine("PAIRED_TLS_OK " + inventory.Displays.Length + " targets");
if (args.Contains("--auth-denial")) {
    using var publicCert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(
        @"C:\Program Files\ATIPiu Labs\PiuLink Host\engine\config\credentials\cacert.pem"));
    using var unpaired = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && peer.RawData.AsSpan().SequenceEqual(publicCert.RawData) });
    using var plain = await unpaired.GetAsync(new UriBuilder("http", endpoint.Host, 48011, "/v1/displays").Uri, timeout.Token);
    if (plain.StatusCode != System.Net.HttpStatusCode.Forbidden) throw new IOException("Plaintext display access was not rejected.");
    try {
        using var tls = await unpaired.GetAsync(new UriBuilder("https", endpoint.Host, 48011, "/v1/displays").Uri, timeout.Token);
        if (tls.IsSuccessStatusCode) throw new IOException("Unpaired TLS display access succeeded.");
    } catch (HttpRequestException) { }
    Console.WriteLine("PLAINTEXT_AND_UNPAIRED_TLS_DENIED"); return;
}
if (args.Contains("--expect-busy")) {
    var baseline = DisplayTopologyService.ReadDesktopState().Layout;
    try { await companion.PrepareAsync(endpoint, inventory.Displays.First(d => d.IsVirtual), StreamProfile.Default with { Resolution = "1920x1080" }, timeout.Token); }
    catch (IOException) when (overview.HasActiveStreams) {
        if (DisplayTopologyService.ReadDesktopState().Layout != baseline) throw new IOException("Busy rejection changed topology.");
        Console.WriteLine("ACTIVE_STREAM_REJECTED_WITHOUT_MUTATION"); return;
    }
    throw new IOException("Expected active-stream rejection.");
}
if (args.Contains("--prepare-abort") || args.Contains("--qt-stream")) {
    if (overview.HasActiveStreams) throw new IOException("A stream is active; no display changes are allowed.");
    var fps = args.Contains("--120fps") ? 120 : 60;
    var target = inventory.Displays.First(d => d.IsVirtual && d.Modes!.Any(m => m.Resolution == "1920x1080" && m.Fps == fps));
    var baseline = DisplayTopologyService.ReadDesktopState().Layout;
    try {
        await companion.PrepareAsync(endpoint, target, StreamProfile.Default with { Resolution = "1920x1080", FramesPerSecond = fps }, timeout.Token);
        Console.WriteLine("PREPARED " + DisplayTopologyService.ReadDesktopState().Layout);
        if (args.Contains("--hold-prepared")) {
            var prepared = DisplayTopologyService.ReadDesktopState().Layout;
            await Task.Delay(8000);
            if (prepared != DisplayTopologyService.ReadDesktopState().Layout) throw new IOException("Preparing grace period reverted the display prematurely.");
            Console.WriteLine("PREPARING_GRACE_VERIFIED");
        }
        if (args.Contains("--qt-stream")) {
            var qt = new PiuLink.Client.Core.Streaming.MoonlightQtCommandService(@"C:\Program Files\ATIPiu Labs\PiuLink Client\moonlight-qt\PiuLink-Moonlight.exe");
            await using var stream = await qt.LaunchStreamAsync(endpoint, "Desktop", StreamProfile.Default with { Resolution = "1920x1080", FramesPerSecond = 60 }, timeout.Token);
            await Task.Delay(3000);
            if (!(await api.GetOverviewAsync(credential.Username, credential.Secret)).HasActiveStreams) throw new IOException("Qt did not establish an active stream.");
            Console.WriteLine("QT_PAIRED_LEASE_STREAM_OK");
            stream.Stop(); await stream.WaitForExitAsync(timeout.Token);
        }
    } finally { await companion.AbortAsync(endpoint); }
    for (int attempt = 0; attempt < 45; attempt++) {
        if (DisplayTopologyService.ReadDesktopState().Layout == baseline && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiuLink", "DisplayRecovery", "transaction.json"))) {
            Console.WriteLine("RECOVERY_VERIFIED"); return;
        }
        await Task.Delay(1000);
    }
    throw new IOException("Desktop recovery did not finish within the verification window.");
}
