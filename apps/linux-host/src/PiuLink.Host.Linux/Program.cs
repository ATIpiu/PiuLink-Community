using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using PiuLink.Host.Core.Diagnostics;
using PiuLink.Host.Linux;
using PiuLink.Host.Services;

[assembly: SupportedOSPlatform("linux")]

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("PiuLink Host Linux requires Linux.");
    return 1;
}

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        PiuLink Host Linux 0.1.4
        doctor | apps | clients | config | pin | peers | send <device-name-or-fingerprint> <file> [file ...]
        ssh-pending | ssh-approve <request-id> <ssh-key-fingerprint>
        invite | invite-off | capture-index | serve
        """);
    return 0;
}

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var data = Environment.GetEnvironmentVariable("PIULINK_DATA")
    ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "piulink-linux");
var sunshine = Environment.GetEnvironmentVariable("PIULINK_ENGINE_CONFIG")
    ?? Path.Combine(data, "config", "sunshine");
var state = new HostState(data, sunshine);
var invitations = new InviteRegistration(data);
using var cancellation = new CancellationTokenSource();
using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;

try
{
    switch (args)
    {
        case ["invite"]:
            Console.WriteLine(await invitations.EnableAsync(token));
            break;
        case ["invite-off"]:
            invitations.Disable();
            Console.WriteLine("Registration stopped. The old invitation expires within 90 seconds.");
            break;
        case ["capture-index"]:
            var capture = await X11Display.ReadAsync(token)
                ?? throw new IOException("No active X11 output matches the capture setting. Check xrandr --query and PIULINK_OUTPUT_INDEX.");
            Console.WriteLine(capture.CaptureIndex);
            break;
        case ["serve"]:
            using (var identity = state.LoadCertificate()) { }
            using (var log = new LocalLog(Path.Combine(data, "logs")))
            using (var transfers = new FileTransferServer(state.ValidateTransfer, log, state.IsPaired,
                state.LoadCertificate, state.Inbox, useIpv6: false))
            {
                if (!transfers.Start()) throw new IOException($"Cannot listen on TCP {FileTransferServer.Port}.");
                using var registration = CancellationTokenSource.CreateLinkedTokenSource(token);
                var heartbeat = invitations.MaintainAsync(registration.Token);
                try { await new Companion(state).RunAsync(token); }
                finally
                {
                    registration.Cancel();
                    try { await heartbeat; } catch (OperationCanceledException) when (registration.IsCancellationRequested) { }
                }
            }
            break;
        case ["doctor"]:
        {
            bool webReady;
            try
            {
                using var control = new SunshineControl(state);
                await control.RequestAsync("api/config", token: token);
                webReady = true;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or System.Security.Cryptography.CryptographicException)
            { webReady = false; }
            var display = await X11Display.ReadAsync(token);
            var uinput = WritableUinput();
            var headless = Environment.GetEnvironmentVariable("PIULINK_SESSION_MODE") == "headless";
            var inputBackend = headless
                ? (X11InputReady(data) ? "xtest" : "unavailable")
                : (uinput ? "uinput" : "unavailable");
            Print(new
            {
                host = Environment.MachineName, platform = "linux", engineReady = webReady, display,
                encoder = Environment.GetEnvironmentVariable("PIULINK_ENCODER") ?? "auto",
                inputBackend, inputDeviceWritable = uinput,
                ssh = new { username = Environment.UserName, port = state.SshPort },
                network = state.NetworkStatus(), inbox = state.Inbox,
                tailscale = File.Exists("/sys/class/net/tailscale0/operstate"),
                dataDirectory = state.DirectoryPath
            });
            return webReady && display is not null && inputBackend != "unavailable" ? 0 : 1;
        }
        case ["apps" or "clients" or "config"]:
            using (var control = new SunshineControl(state))
            {
                var path = args[0] == "clients" ? "api/clients/list" : "api/" + args[0];
                Print(await control.RequestAsync(path, token: token));
            }
            break;
        case ["pin"]:
            Console.Error.Write("Client pairing PIN: ");
            var pin = Console.ReadLine();
            if (pin is null || pin.Length != 4 || !pin.All(char.IsAsciiDigit))
                throw new ArgumentException("Enter exactly four digits.");
            using (var control = new SunshineControl(state))
                await control.RequestAsync("api/pin", new { pin, name = "PiuLink client" }, token);
            Console.WriteLine("PIN submitted.");
            break;
        case ["peers"]:
            Print(state.Recipients());
            break;
        case ["send", var selector, .. var files] when files.Length > 0:
        {
            var recipient = state.ResolveRecipient(selector);
            const string needsGrant = "请在收件设备打开 PiuLink，点一下这台 Linux 主机以建立文件授权，然后重试。";
            if (!recipient.TransferReady) throw new IOException(needsGrant);
            using var identity = state.LoadCertificate();
            for (int i = 0; i < files.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                // Another connection may have refreshed the address or revoked trust between files.
                var peer = state.ReadPeers().SingleOrDefault(p => p.Fingerprint == recipient.Fingerprint);
                if (peer == null || !state.IsPaired(recipient.Fingerprint)) throw new IOException(needsGrant);
                var name = Path.GetFileName(files[i]);
                Console.Error.WriteLine($"[{i + 1}/{files.Length}] {name} → {peer.Name}");
                await using var file = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await FileTransferSender.SendToPeerAsync(file, file.Length, name,
                    peer.DeviceId, peer.Token, peer.Fingerprint, peer.Addresses, identity, state.IsPaired, token, new SendProgress());
                Console.WriteLine($"[{i + 1}/{files.Length}] {name} · 对方已确认保存");
            }
            break;
        }
        case ["ssh-pending"]:
        {
            var request = new SshApproval(state).Pending();
            Print(request is null ? null : new { request.Id, request.Device, request.Address, request.Fingerprint, request.ExpiresAt });
            break;
        }
        case ["ssh-approve", var id, var sshFingerprint]:
            await new SshApproval(state).ApproveAsync(id, sshFingerprint, token);
            Console.WriteLine("SSH public key authorized for " + Environment.UserName + ".");
            break;
        default:
            Console.Error.WriteLine("Invalid command. Run with --help.");
            return 2;
    }
    return 0;
}
catch (OperationCanceledException) when (token.IsCancellationRequested) { return 130; }
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static void Print(object? value) => Console.WriteLine(JsonSerializer.Serialize(value,
    new JsonSerializerOptions(HostState.Json) { WriteIndented = true }));

static bool WritableUinput()
{
    try
    {
        using var device = File.OpenHandle("/dev/uinput", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        return !device.IsInvalid;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
}

static bool X11InputReady(string data)
{
    try
    {
        var status = File.ReadAllText(Path.Combine(data, "config", "headless-input.ready"));
        return int.TryParse(status, out var pid) && pid > 0 && Directory.Exists($"/proc/{pid}");
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
}

sealed class SendProgress : IProgress<(long Sent, long Total)>
{
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    public void Report((long Sent, long Total) bytes)
    {
        if (bytes.Sent == bytes.Total)
            Console.Error.WriteLine($"已发送 {bytes.Total:N0} 字节，等待对方保存…");
        else if (clock.ElapsedMilliseconds >= 1000)
        {
            Console.Error.WriteLine($"{bytes.Sent * 100 / bytes.Total}% · {bytes.Sent:N0} / {bytes.Total:N0} 字节");
            clock.Restart();
        }
    }
}
