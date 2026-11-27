using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using PiuLink.Host.Core.Models;

namespace PiuLink.Host.Core.Diagnostics;

public sealed class HostDiagnosticsService(
    TailscaleProbe tailscaleProbe,
    SunshineProbe sunshineProbe,
    GpuProbe gpuProbe,
    SunshineEndpointProbe sunshineEndpointProbe,
    FirewallProbe firewallProbe)
{
    public static HostDiagnosticsService CreateDefault()
    {
        var runner = new ProcessRunner();
        return new HostDiagnosticsService(
            new TailscaleProbe(runner),
            new SunshineProbe(runner),
            new GpuProbe(runner),
            new SunshineEndpointProbe(),
            new FirewallProbe(runner));
    }

    public async Task<HostSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        var tailscaleTask = tailscaleProbe.CheckAsync(cancellationToken);
        var sunshineTask = sunshineProbe.CheckAsync(cancellationToken);
        var gpuTask = gpuProbe.CheckAsync(cancellationToken);
        var endpointTask = sunshineEndpointProbe.CheckAsync(cancellationToken);
        var firewallTask = firewallProbe.CheckAsync(cancellationToken);
        await Task.WhenAll(tailscaleTask, sunshineTask, gpuTask, endpointTask, firewallTask);

        var tailscale = await tailscaleTask;
        return new HostSnapshot(
            DateTimeOffset.Now,
            tailscale.DeviceName ?? Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture,
            await gpuTask,
            tailscale.Health,
            await sunshineTask,
            tailscale.IpAddress,
            await endpointTask,
            await firewallTask,
            ReadLanAddresses());
    }

    private static string[] ReadLanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                    !adapter.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Select(ip => ip.ToString()).Distinct().ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    public static string FormatReport(HostSnapshot snapshot)
    {
        var report = new StringBuilder()
            .AppendLine("PiuLink Host diagnostics")
            .AppendLine($"Checked: {snapshot.CheckedAt:O}")
            .AppendLine($"Device: {snapshot.DeviceName}")
            .AppendLine($"Windows: {snapshot.WindowsVersion}")
            .AppendLine($"Architecture: {snapshot.Architecture}")
            .AppendLine($"Ready: {snapshot.IsReady}")
            .AppendLine($"Tailscale: {snapshot.Tailscale.State} — {snapshot.Tailscale.Summary}")
            .AppendLine($"Sunshine: {snapshot.Sunshine.State} — {snapshot.Sunshine.Summary}")
            .AppendLine($"Sunshine endpoint: {snapshot.SunshineEndpoint?.State} — {snapshot.SunshineEndpoint?.Summary}")
            .AppendLine($"Windows Firewall: {snapshot.Firewall?.State} — {snapshot.Firewall?.Summary}")
            .AppendLine($"LAN addresses: {string.Join(", ", snapshot.LanAddresses ?? [])}")
            .AppendLine($"Overlay address: {snapshot.TailscaleIp ?? "Unavailable"}")
            .AppendLine("Display adapters:");

        foreach (var gpu in snapshot.Gpus)
        {
            report.AppendLine($"- {gpu.Name} ({gpu.Vendor})");
        }

        return report.ToString();
    }
}
