using System.Net;
using System.Net.Sockets;

namespace PiuLink.Client.Core.Networking;

public sealed class WakeOnLanService
{
    private static readonly int[] StaticPorts = [9, 47009];
    private static readonly int[] DynamicPorts = [47998, 47999, 48000, 48002, 48010];

    public static byte[] CreateMagicPacket(string macAddress)
    {
        var compact = new string(macAddress.Where(Uri.IsHexDigit).ToArray());
        if (compact.Length != 12 || !compact.All(Uri.IsHexDigit))
            throw new FormatException("MAC address must contain exactly six hexadecimal bytes.");
        var mac = Convert.FromHexString(compact);
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var offset = 6; offset < packet.Length; offset += mac.Length)
            Buffer.BlockCopy(mac, 0, packet, offset, mac.Length);
        return packet;
    }

    public async Task SendAsync(HostEndpoint endpoint, string macAddress, CancellationToken token = default)
    {
        var packet = CreateMagicPacket(macAddress);
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var destinations = new HashSet<IPAddress> { IPAddress.Broadcast };
        try {
            foreach (var address in await Dns.GetHostAddressesAsync(endpoint.Host, token))
                if (address.AddressFamily == AddressFamily.InterNetwork) destinations.Add(address);
        } catch (SocketException) { }
        var ports = StaticPorts.Concat(DynamicPorts.Select(port => port - 47989 + endpoint.Port)).Where(port => port is >= 1 and <= 65535).Distinct();
        var sent = 0;
        foreach (var address in destinations)
            foreach (var port in ports) {
                token.ThrowIfCancellationRequested();
                try { await socket.SendAsync(packet, new IPEndPoint(address, port), token); sent++; }
                catch (SocketException) { }
            }
        if (sent == 0) throw new IOException("No Wake-on-LAN packet could be sent.");
    }
}
