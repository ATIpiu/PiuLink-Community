using System.Net.NetworkInformation;
using PiuLink.Host.Core.WakeOnLan;

namespace PiuLink.Host.Core.Tests;

internal static class WakeOnLanServiceTests
{
    public static void Run()
    {
        ParseWakeArmedDevicesIgnoresBlankLinesAndNone();
        FormatMacAddressUsesWakeOnLanNotation();
    }

    private static void ParseWakeArmedDevicesIgnoresBlankLinesAndNone()
    {
        var devices = WakeOnLanService.ParseWakeArmedDevices(
            "Intel(R) Ethernet Controller\r\n\r\nNONE\r\nRealtek PCIe GbE Family Controller\r\n");
        Assert(devices.SetEquals([
            "Intel(R) Ethernet Controller",
            "Realtek PCIe GbE Family Controller"
        ]), "wake-armed device parsing should retain only device names");
    }

    private static void FormatMacAddressUsesWakeOnLanNotation()
    {
        var formatted = WakeOnLanService.FormatMacAddress(
            new PhysicalAddress([0x00, 0x1A, 0x2B, 0x03, 0x04, 0xF5]));
        Assert(formatted == "00-1A-2B-03-04-F5", "MAC addresses should be uppercase and hyphen separated");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
