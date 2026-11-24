using System.Net.NetworkInformation;
using System.Net.Sockets;
using PiuLink.Host.Core.Diagnostics;

namespace PiuLink.Host.Core.WakeOnLan;

public sealed record WakeOnLanAdapter(
    string Name,
    string Description,
    string MacAddress,
    IReadOnlyList<string> Ipv4Addresses,
    bool IsWakeArmed);

public sealed record WakeOnLanStatus(
    IReadOnlyList<WakeOnLanAdapter> Adapters,
    bool PowerQuerySucceeded,
    string? Error)
{
    public bool HasCandidate => Adapters.Count > 0;
    public bool HasArmedAdapter => Adapters.Any(adapter => adapter.IsWakeArmed);
}

public sealed class WakeOnLanService(IProcessRunner processRunner)
{
    public static WakeOnLanService CreateDefault() => new(new ProcessRunner());

    public async Task<WakeOnLanStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NetworkInterface> candidates;
        try
        {
            candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsCandidate)
                .OrderBy(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (NetworkInformationException exception)
        {
            return new WakeOnLanStatus([], false, exception.Message);
        }

        var powerQuery = await processRunner.RunAsync(
            "powercfg.exe",
            ["/devicequery", "wake_armed"],
            TimeSpan.FromSeconds(8),
            cancellationToken);
        var armedDevices = powerQuery.Succeeded
            ? ParseWakeArmedDevices(powerQuery.StandardOutput)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var adapters = candidates.Select(adapter => new WakeOnLanAdapter(
            adapter.Name,
            adapter.Description,
            FormatMacAddress(adapter.GetPhysicalAddress()),
            adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address.ToString())
                .ToArray(),
            armedDevices.Contains(adapter.Name) || armedDevices.Contains(adapter.Description)))
            .ToArray();

        var error = powerQuery.Succeeded
            ? null
            : powerQuery.TimedOut
                ? "Windows power-device detection timed out."
                : string.IsNullOrWhiteSpace(powerQuery.StandardError)
                    ? "Windows could not inspect wake-enabled devices."
                    : powerQuery.StandardError.Trim();
        return new WakeOnLanStatus(adapters, powerQuery.Succeeded, error);
    }

    public static HashSet<string> ParseWakeArmedDevices(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.Equals(line, "NONE", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string FormatMacAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "Unavailable" : string.Join("-", bytes.Select(value => value.ToString("X2")));
    }

    private static bool IsCandidate(NetworkInterface adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up &&
        adapter.GetPhysicalAddress().GetAddressBytes().Length == 6 &&
        adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
}
