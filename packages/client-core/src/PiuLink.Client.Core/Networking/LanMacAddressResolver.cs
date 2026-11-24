using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PiuLink.Client.Core.Networking;

public static class LanMacAddressResolver
{
    public static async Task<string?> ResolveAsync(string host, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            foreach (var address in addresses.Where(value => value.AddressFamily == AddressFamily.InterNetwork))
            {
                token.ThrowIfCancellationRequested();
                var bytes = address.GetAddressBytes();
                var destination = BitConverter.ToUInt32(bytes, 0);
                var mac = new byte[6];
                var length = mac.Length;
                if (SendARP(destination, 0, mac, ref length) == 0 && length == 6 && mac.Any(value => value != 0))
                    return string.Join(':', mac.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
            }
            return null;
        }, token).ConfigureAwait(false);
    }

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(uint destinationIp, uint sourceIp, byte[] macAddress, ref int physicalAddressLength);
}
