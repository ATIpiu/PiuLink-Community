using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;

namespace PiuLink.Client.Core.Networking;

public sealed record DiscoveredHost(string Name, string Address);

public sealed class LanHostDiscoveryService
{
    public async Task<IReadOnlyList<DiscoveredHost>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is >= 24 and <= 30 && IsLan(u.Address))
            .SelectMany(u => Candidates(u.Address, u.PrefixLength))
            .Distinct().ToArray();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        return await ProbeAddressesAsync(client, addresses, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<DiscoveredHost>> ProbeAddressesAsync(HttpClient client,
        IEnumerable<string> addresses, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(48);
        var results = await Task.WhenAll(addresses.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await ProbeAsync(client, address, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return results.Where(r => r is not null).Cast<DiscoveredHost>().OrderBy(r => r.Name).ToArray();
    }

    private static bool IsLan(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }

    private static IEnumerable<string> Candidates(IPAddress address, int prefix)
    {
        var value = BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray());
        var mask = uint.MaxValue << (32 - prefix); var network = value & mask; var broadcast = network | ~mask;
        for (var candidate = network + 1; candidate < broadcast; candidate++)
            yield return new IPAddress(BitConverter.GetBytes(candidate).Reverse().ToArray()).ToString();
    }

    private static async Task<DiscoveredHost?> ProbeAsync(HttpClient client, string address, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(450);
        try
        {
            using var response = await client.GetAsync($"http://{address}:47989/serverinfo",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > 32768) return null;
                output.Write(buffer, 0, count);
            }
            var xml = System.Text.Encoding.UTF8.GetString(output.ToArray());
            var root = XDocument.Parse(xml); var version = root.Descendants("appversion").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(version)) return null;
            var name = root.Descendants("hostname").FirstOrDefault()?.Value;
            return new(string.IsNullOrWhiteSpace(name) ? address : name.Trim(), address);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Xml.XmlException) { return null; }
    }
}
