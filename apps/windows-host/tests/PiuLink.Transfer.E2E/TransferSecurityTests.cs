using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PiuLink.Host.Services;

internal static class TransferSecurityTests
{
    public static async Task RunAsync()
    {
        var inbox = Directory.CreateTempSubdirectory("PiuLinkTransferTest-");
        try {
            string Commit(byte value) {
                var temp = Path.Combine(inbox.FullName, Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(temp, [value]);
                return FileTransferServer.CommitUnique(temp, inbox.FullName, "report.bin");
            }
            var saved = Commit(1);
            var copies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Commit(1))));
            Check(copies.All(file => file == saved), "Concurrent retries saved multiple copies");
            var changed = Commit(2);
            Check(saved != changed && Commit(2) == changed && inbox.GetFiles().Length == 2, "Same-name content collision");
            Console.WriteLine("PASS concurrent retries, lost confirmation and same-name files with different contents.");
        } finally { inbox.Delete(recursive: true); }
        using var host = CreateCertificate("Test Host");
        using var phone = CreateCertificate("Test Phone");
        var phoneId = FileTransferTls.Fingerprint(phone);
        var hostId = FileTransferTls.Fingerprint(host);
        const string device = "android-fixture";
        var path = @"Software\ATIPiu Labs\PiuLink Transfer Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var store = new FileTransferPairingStore(path);
            var oldToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            using (var key = Registry.CurrentUser.CreateSubKey(path))
                key.SetValue("FileTransferDevices", JsonSerializer.Serialize(new Dictionary<string, string> {
                    [device] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(oldToken))) }));
            var request = new FileTransferPairRequest(device, "Phone", null, "127.0.0.1", true, phoneId);
            Check(store.Bind(request) is null, "Unproven legacy binding");
            var first = store.Bind(request with { Token = oldToken });
            Check(first is not null && first != oldToken && store.Validate(device, first, phoneId), "Legacy token migration");
            Check(store.Bind(request with { RemoteAddress = "127.0.0.2" }) == first, "Refresh changed the bound token");
            var second = store.Bind(request with { CertificateFingerprint = hostId, DeviceName = "Other fixture" });
            Check(second is not null && first != second && !store.Validate(device, first!, hostId) &&
                store.Validate(device, second, hostId) && store.Peers().Count == 2, "Device ID collision");
            var peer = store.Peers().Single(p => p.CertificateFingerprint == phoneId);
            await TransferAsync(host, phone, peer, store, reverse: false);
            await TransferAsync(host, phone, peer, store, reverse: true);
            await RejectPeerAsync(host, phone, wrongCertificate: true);
            await RejectPeerAsync(host, phone, wrongCertificate: false);
            Console.WriteLine("PASS transfer TLS refuses mismatched and removed peer certificates");
            Console.WriteLine("PASS encrypted file payload and save acknowledgement in both TLS roles");
            Console.WriteLine("PASS legacy token migration, address refresh and independent certificate bindings");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    private static async Task TransferAsync(X509Certificate2 host, X509Certificate2 phone,
        FileTransferPairingStore.Peer peer, FileTransferPairingStore store, bool reverse)
    {
        var sender = reverse ? phone : host;
        var receiver = reverse ? host : phone;
        var expectedSender = FileTransferTls.Fingerprint(sender);
        var expectedReceiver = FileTransferTls.Fingerprint(receiver);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var payload = RandomNumberGenerator.GetBytes(256 * 1024 + 17);
        const string filename = "双向回归.bin";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var receive = ReceiveAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        await using var outgoing = FileTransferTls.Create(client.GetStream(), expectedReceiver,
            cert => cert is not null && FileTransferTls.Fingerprint(cert) == expectedReceiver);
        await FileTransferTls.ConnectAsync(outgoing, sender, "localhost", ct);
        using var input = new MemoryStream(payload);
        await Task.WhenAll(receive, FileTransferSender.SendPayloadAsync(outgoing, input, input.Length, filename, peer, ct));

        async Task ReceiveAsync()
        {
            using var accepted = await listener.AcceptTcpClientAsync(ct);
            await using var incoming = FileTransferTls.Create(accepted.GetStream(), null,
                cert => cert is not null && FileTransferTls.Fingerprint(cert) == expectedSender);
            await FileTransferTls.AcceptAsync(incoming, receiver, ct);
            Check(incoming.IsMutuallyAuthenticated && incoming.IsEncrypted, "Mutual TLS");
            var header = new byte[22];
            await incoming.ReadExactlyAsync(header, ct);
            Check(header.AsSpan(0, 8).SequenceEqual("PIULINK1"u8), "Magic");
            async Task<string> ReadField(int offset) {
                var field = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset))];
                await incoming.ReadExactlyAsync(field, ct);
                return Encoding.UTF8.GetString(field);
            }
            var deviceId = await ReadField(8);
            var token = await ReadField(10);
            Check(store.Validate(deviceId, token, peer.CertificateFingerprint!), "Bound credentials");
            Check(await ReadField(12) == filename && BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(14)) == payload.Length, "File metadata");
            var received = new byte[payload.Length];
            await incoming.ReadExactlyAsync(received, ct);
            Check(SHA256.HashData(payload).AsSpan().SequenceEqual(SHA256.HashData(received)), "Saved bytes differ");
            await incoming.WriteAsync(new byte[] { 1 }, ct);
            await incoming.FlushAsync(ct);
        }
    }

    private static async Task RejectPeerAsync(X509Certificate2 host, X509Certificate2 phone, bool wrongCertificate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = stop.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = AcceptAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
        var pin = FileTransferTls.Fingerprint(wrongCertificate ? host : phone);
        using (var connection = FileTransferTls.Create(client.GetStream(), pin, _ => wrongCertificate))
        {
            try
            {
                await FileTransferTls.ConnectAsync(connection, host, "localhost", token);
                throw new InvalidOperationException("Transfer started with an untrusted peer");
            }
            catch (System.Security.Authentication.AuthenticationException) { }
        }
        await accepting;

        async Task AcceptAsync()
        {
            using var socket = await listener.AcceptTcpClientAsync(token);
            using var incoming = FileTransferTls.Create(socket.GetStream(), FileTransferTls.Fingerprint(host), _ => true);
            try { await FileTransferTls.AcceptAsync(incoming, phone, token); }
            catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException) { }
        }
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.UserKeySet);
    }

    private static void Check(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
