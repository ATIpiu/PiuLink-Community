using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace PiuLink.Rendezvous;

public static class InvitePublisher
{
    public static string NewOwner() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static async Task<HostInvite> PublishAsync(string owner, CancellationToken token, string serviceBase = HostInvite.Base)
    {
        var id = await HostInvite.ReadHostIdAsync(IPAddress.Loopback, token).ConfigureAwait(false);
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).Where(HostInvite.IsCandidate).Distinct()
            .Take(12).Select(ip => ip.ToString()).ToArray();
        var invite = HostInvite.FromOwner(owner, id, serviceBase);
        using var request = invite.Post("publish", owner);
        request.Content = JsonContent.Create(new HostInvite.Presence(id, addresses));
        using var response = await HostInvite.DirectoryClient.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return invite;
    }
}
