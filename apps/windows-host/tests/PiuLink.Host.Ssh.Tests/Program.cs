using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PiuLink.Host.Services;
using PiuLink.Host.Core.Sunshine;

if (args.Contains("--probe-ssh")) {
    Console.WriteLine(JsonSerializer.Serialize(await SshTerminalSetup.ProbeAsync()));
    return;
}
await RemoteMicrophoneTests.Run(args.Contains("--microphone-device-test"));
var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository != null && !File.Exists(Path.Combine(repository.FullName, "PiuLink.sln"))) repository = repository.Parent;
await CompanionContractTests.Run(Path.Combine(repository!.FullName, "contracts", "companion-v1.json"), args.Contains("--export-companion-contract"));

if (!SshTerminalSetup.EnableScript.Contains("InstallPending", StringComparison.Ordinal) ||
    !SshTerminalSetup.EnableScript.Contains("Get-Service sshd", StringComparison.Ordinal) ||
    !SshTerminalSetup.EnableScript.Contains("exit 10", StringComparison.Ordinal))
    throw new Exception("OpenSSH setup must distinguish a pending Windows component from a ready sshd service.");
Console.WriteLine("PASS: OpenSSH setup reports pending component registration instead of failing at Set-Service.");

// Exercise the real request handler on an ephemeral loopback port, without touching the installed Host.
using var server = new DisplayCompanionServer(_ => Task.FromResult<SunshineDisplayState?>(null),
    (_, _, _, _, _) => Task.FromResult(false));
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
    var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
    var handler = typeof(DisplayCompanionServer).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var handling = (Task)handler.Invoke(server, [accepted, timeout.Token])!;
    var bytes = Encoding.ASCII.GetBytes("GET /v1/ssh HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
    await client.GetStream().WriteAsync(bytes, timeout.Token);
    using var reader = new StreamReader(client.GetStream());
    var response = await reader.ReadToEndAsync(timeout.Token);
    await handling;
    if (!response.StartsWith("HTTP/1.1 200")) throw new Exception("SSH defaults endpoint failed");
    using var json = JsonDocument.Parse(response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
    var root = json.RootElement;
    var expected = SshDefaults.Read();
    if (root.GetProperty("username").GetString() != expected.Username || root.GetProperty("port").GetInt32() != expected.Port ||
        root.GetProperty("platform").GetString() != "windows" || root.EnumerateObject().Count() != 3)
        throw new Exception("Incorrect SSH metadata");
    foreach (var port in new[] {0, 65536})
    {
        try { SshDefaults.Save("test", port); throw new Exception("Invalid port accepted"); }
        catch (ArgumentException) { }
    }
    try { SshDefaults.Save("\n", 22); throw new Exception("Invalid user accepted"); }
    catch (ArgumentException) { }
    Console.WriteLine("PASS: SSH endpoint account/port/platform, no secret fields, invalid account/port validation.");
}
finally { listener.Stop(); }

foreach (var invalid in new[] { "ssh-rsa abc;whoami", "ssh-rsa AAAA\nssh-rsa AAAA", "command=evil ssh-rsa AAAA", "ssh-rsa AAAA" })
{
    bool rejected = false;
    try { SshKeyAuthorization.Validate(invalid); } catch (ArgumentException) { rejected = true; } catch (FormatException) { rejected = true; }
    if (!rejected) throw new Exception("Unsafe SSH public key accepted");
}
using (var rsa = System.Security.Cryptography.RSA.Create(3072))
{
    var parameters = rsa.ExportParameters(false);
    using var blob = new MemoryStream();
    void Field(byte[] bytes) {
        var size = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
        blob.Write(size); blob.Write(bytes);
    }
    Field(Encoding.ASCII.GetBytes("ssh-rsa")); Field(parameters.Exponent!);
    Field(new byte[] { 0 }.Concat(parameters.Modulus!).ToArray());
    var key = "ssh-rsa " + Convert.ToBase64String(blob.ToArray()) + " PiuLink-phone";
    if (SshKeyAuthorization.Validate(key) != key) throw new Exception("Valid device key rejected");
}
Console.WriteLine("PASS: RSA device key accepted; multiline, options and command injection rejected.");

if (!SshKeyAuthorization.IsLocalAdministratorAccount(Environment.UserName))
    throw new Exception("Filtered UAC token caused the local administrator account to be misclassified.");
Console.WriteLine("PASS: local administrator account detection is independent of UAC token filtering.");

if (!DisplayCompanionServer.IsTrustedPairingPeer(IPAddress.Parse("192.168.1.13"), true, false) ||
    DisplayCompanionServer.IsTrustedPairingPeer(IPAddress.Parse("192.168.1.13"), false, true) ||
    !DisplayCompanionServer.IsTrustedPairingPeer(IPAddress.Parse("100.78.39.45"), false, true) ||
    DisplayCompanionServer.IsTrustedPairingPeer(IPAddress.Parse("100.78.39.45"), true, false) ||
    !DisplayCompanionServer.IsTrustedPairingPeer(IPAddress.Parse("fd7a:115c:a1e0::1"), false, true) ||
    DisplayCompanionServer.IsTrustedPairingPeer(IPAddress.Parse("8.8.8.8"), true, true))
    throw new Exception("Trusted pairing network policy does not match LAN/Tailscale scopes.");
Console.WriteLine("PASS: LAN and Tailscale address scope classification.");
