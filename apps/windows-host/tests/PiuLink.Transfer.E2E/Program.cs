using PiuLink.Host.Services;
using System.Security.Cryptography;
if (args is ["--self-test"]) {
    await TransferSecurityTests.RunAsync();
    return;
}
if (args.Length is < 2 or > 3 || !File.Exists(args[0])) throw new ArgumentException("Specify a synthetic test file, the paired device ID, and optionally --stale-snapshot.");
var peers = new FileTransferPairingStore().Peers();
var peer = peers.Single(p => p.DeviceId == args[1]);
if (args.Length == 3) {
    if (args[2] != "--stale-snapshot") throw new ArgumentException("Unknown test option.");
    // Simulate a still-open dialog from before a phone re-paired and changed networks.
    // The invalid cached token must never be decrypted or sent to the cached address.
    peer = peer with { Address = "192.0.2.1", ProtectedToken = "invalid-stale-snapshot" };
}
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var started = DateTime.UtcNow;
try {
    await FileTransferSender.SendAsync(peer, args[0], timeout.Token);
    await using var saved = File.OpenRead(args[0]);
    Console.WriteLine($"ACK saved: {peer.Name}; {Path.GetFileName(args[0])}; {DateTime.UtcNow - started}; SHA256={Convert.ToHexString(await SHA256.HashDataAsync(saved))}");
}
catch (Exception error) {
    Console.WriteLine($"FAILED: {peer.Name}; {DateTime.UtcNow - started}; {error.GetType().Name}: {error.Message}; Cause: {error.GetBaseException().Message}");
    Environment.ExitCode = 1;
}
