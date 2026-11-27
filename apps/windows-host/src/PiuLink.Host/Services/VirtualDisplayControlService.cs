using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PiuLink.Host.Services;

internal sealed record VirtualDisplayDeviceStatus(
    bool Installed,
    bool Enabled,
    string? InstanceId,
    string DisplayName,
    uint? ProblemCode)
{
    public bool StateKnown { get; init; } = true;
}

internal sealed record VirtualDisplayActionResult(
    bool Succeeded,
    bool Cancelled,
    string Message,
    VirtualDisplayDeviceStatus Status);

/// <summary>
/// Controls only the explicitly allow-listed MikeTheTech virtual display device.
/// The UI remains unelevated; Windows displays a UAC prompt for the single
/// enable/disable operation.
/// </summary>
internal sealed partial class VirtualDisplayControlService
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY";
    private const string AllowedHardwareId = @"Root\MttVDD";
    private const uint CrSuccess = 0;
    private const uint DnStarted = 0x00000008;
    private const uint CmProbDisabled = 22;

    public VirtualDisplayDeviceStatus Inspect(string? expectedInstanceId = null)
    {
        using var displayRoot = Registry.LocalMachine.OpenSubKey(EnumRoot);
        if (displayRoot is null)
        {
            return MissingStatus();
        }

        var candidates = new List<VirtualDisplayDeviceStatus>();
        foreach (var deviceKeyName in displayRoot.GetSubKeyNames())
        {
            var instanceId = $@"ROOT\DISPLAY\{deviceKeyName}";
            if (expectedInstanceId != null && !string.Equals(instanceId, expectedInstanceId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!AllowedInstanceId().IsMatch(instanceId))
            {
                continue;
            }

            using var deviceKey = displayRoot.OpenSubKey(deviceKeyName);
            if (deviceKey is null || !HasAllowedHardwareId(deviceKey))
            {
                continue;
            }

            var displayName = deviceKey.GetValue("FriendlyName") as string;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "Virtual Display Driver";
            }

            var locateResult = CM_Locate_DevNode(out var deviceNode, instanceId, 0);
            if (locateResult != CrSuccess)
            {
                candidates.Add(new(true, false, instanceId, displayName, null) { StateKnown = false });
                continue;
            }

            var statusResult = CM_Get_DevNode_Status(
                out var nodeStatus,
                out var problemCode,
                deviceNode,
                0);
            if (statusResult != CrSuccess)
            {
                candidates.Add(new(true, false, instanceId, displayName, null) { StateKnown = false });
                continue;
            }

            var enabled = problemCode != CmProbDisabled && (nodeStatus & DnStarted) != 0;
            candidates.Add(new VirtualDisplayDeviceStatus(
                true,
                enabled,
                instanceId,
                displayName,
                problemCode == 0 ? null : problemCode));
        }

        var present = candidates.Where(candidate => candidate.StateKnown).ToArray();
        if (present.Length > 1) return new(true, present.Any(candidate => candidate.Enabled), null,
            "Multiple virtual display instances; choose an instance before changing driver state.", null) { StateKnown = false };
        return present.FirstOrDefault() ?? candidates.FirstOrDefault() ?? MissingStatus();
    }

    public async Task<VirtualDisplayActionResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var before = Inspect();
        if (!before.Installed || !before.StateKnown || string.IsNullOrWhiteSpace(before.InstanceId))
        {
            return new VirtualDisplayActionResult(
                false,
                false,
                "Install the supported virtual display driver first.",
                before);
        }

        if (before.Enabled == enabled)
        {
            return new VirtualDisplayActionResult(
                true,
                false,
                enabled ? "The virtual display is already active." : "The virtual display is already off.",
                before);
        }

        // Revalidate immediately before elevation. No user-controlled path or argument is accepted.
        if (!IsAllowedInstalledDevice(before.InstanceId))
        {
            return new VirtualDisplayActionResult(
                false,
                false,
                "The installed display device did not match PiuLink's safety allow-list.",
                before);
        }

        var verb = enabled ? "/enable-device" : "/disable-device";
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
            Arguments = $"{verb} \"{before.InstanceId}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new VirtualDisplayActionResult(
                    false,
                    false,
                    "Windows could not start the driver control request.",
                    Inspect());
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return new VirtualDisplayActionResult(
                    false,
                    false,
                    $"Windows returned driver control error {process.ExitCode}.",
                    Inspect());
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new VirtualDisplayActionResult(
                false,
                true,
                "The Windows permission prompt was cancelled.",
                Inspect());
        }

        VirtualDisplayDeviceStatus after = Inspect(before.InstanceId);
        for (var attempt = 0; attempt < 10 && after.Enabled != enabled; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            after = Inspect(before.InstanceId);
        }

        var succeeded = after.StateKnown && after.Installed && after.InstanceId == before.InstanceId && after.Enabled == enabled;
        return new VirtualDisplayActionResult(
            succeeded,
            false,
            succeeded
                ? enabled
                    ? "Virtual display created. Windows can now use it as a screen."
                    : "Virtual display closed safely."
                : "Windows accepted the request, but the display state did not change.",
            after);
    }

    // The supported driver's SETDISPLAYCOUNT command reloads its adapter. Keep
    // that operation off a captured virtual display and verify a new Windows target.
    internal (int? Count, string Status) GetCreationCapability(DisplayTopologyService.SessionOutput[] outputs)
    {
        var device = Inspect();
        if (!device.Installed) return (null, "driver_required");
        if (!device.Enabled || !device.StateKnown) return (null, "enable_driver");
        if (!outputs.Any(o => o.Active && o.Capture && !o.Virtual)) return (null, "physical_capture_required");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver");
            var directory = key?.GetValue("VDDPATH") as string ?? @"C:\VirtualDisplayDriver";
            var document = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "vdd_settings.xml"));
            var count = (int?)document.Root?.Element("monitors")?.Element("count");
            if (count is null or < 0) return (null, "driver_unavailable");
            if (count >= 16) return (null, "limit_reached");
            return (count, "available");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException or OverflowException)
        {
            return (null, "driver_unavailable");
        }
    }

    internal async Task CreateOutputAsync(string captureKey, int expectedCount, CancellationToken token)
    {
        var before = DisplayTopologyService.SessionOutputs(captureKey);
        var capability = GetCreationCapability(before);
        if (capability.Count != expectedCount) throw new InvalidOperationException("outputs_changed");
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "MTTVirtualDisplayPipe",
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, token);
        if (GetCreationCapability(DisplayTopologyService.SessionOutputs(captureKey)).Count != expectedCount)
            throw new InvalidOperationException("outputs_changed");
        var command = System.Text.Encoding.Unicode.GetBytes(FormattableString.Invariant($"SETDISPLAYCOUNT {expectedCount + 1}"));
        await pipe.WriteAsync(command, token);
        await pipe.FlushAsync(token);
        pipe.Close();
        var previousVirtual = before.Where(o => o.Virtual).Select(o => o.Key).ToHashSet();
        for (var attempt = 0; attempt < 36; attempt++)
        {
            await Task.Delay(250, token);
            var after = DisplayTopologyService.SessionOutputs(captureKey);
            if (after.Any(o => o.Active && o.Capture) &&
                after.Count(o => o.Virtual) > previousVirtual.Count &&
                previousVirtual.IsSubsetOf(after.Where(o => o.Virtual).Select(o => o.Key))) return;
        }
        throw new InvalidOperationException("creation_unconfirmed");
    }

    private static bool IsAllowedInstalledDevice(string instanceId)
    {
        if (!AllowedInstanceId().IsMatch(instanceId))
        {
            return false;
        }

        var keyName = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
        using var deviceKey = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{keyName}");
        return deviceKey is not null && HasAllowedHardwareId(deviceKey);
    }

    private static bool HasAllowedHardwareId(RegistryKey deviceKey)
    {
        var value = deviceKey.GetValue("HardwareID");
        var hardwareIds = value switch
        {
            string one => new[] { one },
            string[] many => many,
            _ => Array.Empty<string>()
        };

        return hardwareIds.Any(id =>
            string.Equals(id, AllowedHardwareId, StringComparison.OrdinalIgnoreCase));
    }

    private static VirtualDisplayDeviceStatus MissingStatus() =>
        new(false, false, null, "Virtual Display Driver", null);

    [GeneratedRegex(@"^ROOT\\DISPLAY\\[0-9A-F]{4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedInstanceId();

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(
        out uint deviceInstance,
        string deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(
        out uint status,
        out uint problemNumber,
        uint deviceInstance,
        uint flags);
}
