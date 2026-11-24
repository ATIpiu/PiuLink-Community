using System.Runtime.InteropServices;
using PiuLink.Host.Services;
using static PiuLink.Host.Services.DisplayTopologyService;


static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Unsafe topology was accepted.");
}
static void RejectArgument(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("Invalid topology dimensions were accepted.");
}

Require(Marshal.SizeOf<DisplayPath>() == 72, "DISPLAYCONFIG_PATH_INFO layout mismatch");
Require(WindowsCredentialStore.PersistenceScope == 2,
    "PiuLink credentials must use Windows local-machine persistence");
Require(typeof(WindowsCredentialStore).GetMethod(
        "Delete",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic) is null,
    "Application code must not expose a PiuLink credential deletion path");
Require(DpiProbe.AreDpiAwarenessContextsEqual(DpiProbe.GetThreadDpiAwarenessContext(), new IntPtr(-4)),
    "Display operations must run in the Host's PerMonitorV2 coordinate space");
Require(Marshal.SizeOf<Mode>() == 64, "DISPLAYCONFIG_MODE_INFO layout mismatch");
Require(DisplayTopologyService.IsVirtualIdentity(
        "S2719DGF", "GameViewer Virtual Display Adapter", @"Root\GameViewerIddDriver"),
    "A virtual adapter with a spoofed physical monitor EDID was misclassified");
Require(DisplayTopologyService.IsVirtualIdentity(
        "VDD by MTT", "Virtual Display Driver", @"Root\MttVDD"),
    "The supported MTT virtual display was misclassified");
Require(!DisplayTopologyService.IsVirtualIdentity(
        "S2719DGF", "NVIDIA GeForce RTX 5090 D", @"PCI\VEN_10DE&DEV_2B87"),
    "A physical GPU output was misclassified as virtual");
Require(DisplayTopologyService.MatchesRequestedDisplay(
        @"\\.\DISPLAY5", "VDD by MTT", "", "VDD by MTT"),
    "An attached selected VDD was not resolved from its stable friendly name");
Require(!DisplayTopologyService.MatchesRequestedDisplay(
        @"\\.\DISPLAY6", "S2719DGF", "", "VDD by MTT"),
    "Another vendor's sole active virtual display was accepted as the selected VDD");
var capture = new SessionOutput("capture", "A", true, false, true);
var secondary = new SessionOutput("second", "B", false, true, false);
Require(!CanSetOutput([capture], "capture", false), "Cannot disable the captured or last output");
Require(CanSetOutput([capture, secondary], "second", true), "An inactive secondary can be enabled");
Require(CanSetOutput([capture, secondary with { Active = true }], "second", false), "A secondary can be disabled");
Require(!CanSetOutput([secondary], "second", true) && !CanSetOutput([capture], "absent", true),
    "An unknown capture or stale target must block the live edit");
var attached = new PiuLink.Host.Core.Sunshine.SunshineDisplay("a", "A", "A", true, false, 1920, 1080, 60);
var detached = attached with { DeviceId = "b", DisplayName = "B", Width = null, Height = null };
var fast = new PiuLink.Host.Core.Sunshine.SunshineDisplayPreparation(new([attached, detached], "b", true, true), 0, 0, "{}");
Require(fast.CanUseSolePhysicalOutput(attached, 1) && !fast.CanUseSolePhysicalOutput(detached, 1) &&
    !fast.CanUseSolePhysicalOutput(attached, 2) &&
    !(fast with { ActiveStreams = 1 }).CanUseSolePhysicalOutput(attached, 1), "Sole-output route selection mismatch");
var virtualOutput = attached with { IsVirtual = true };
Require(!(fast with { State = fast.State with { Displays = [virtualOutput] } }).CanUseSolePhysicalOutput(virtualOutput, 1),
    "Virtual mode negotiation must always run");
Require(!(fast with { State = fast.State with { Displays = [attached, attached with { DeviceId = "c" }] } }).CanUseSolePhysicalOutput(attached, 1),
    "Windows and the engine must both see exactly one live output");
var leasedDisplay = attached with { DeviceId = Guid.NewGuid().ToString("B"), IsPrimary = false, Bounds = new(-1920, 200, 1920, 1080) };
var extended = fast with { SupportsSessionOutput = true, State = fast.State with { Displays = [attached, leasedDisplay] } };
Require(extended.CanCaptureExtendedOutput(leasedDisplay, false) &&
    !extended.CanCaptureExtendedOutput(leasedDisplay, true) && !extended.CanCaptureExtendedOutput(detached, false) &&
    !(extended with { SupportsSessionOutput = false }).CanCaptureExtendedOutput(leasedDisplay, false) &&
    !(extended with { ActiveStreams = 1 }).CanCaptureExtendedOutput(leasedDisplay, false),
    "Per-session extended capture requires engine support, an idle stream and a connected physical target");
var decodedScale = DisplayTopologyService.DecodeDisplayScale(-2, -1, 5);
Require(decodedScale.RecommendedPercent == 150 && decodedScale.CurrentPercent == 125 &&
    decodedScale.SupportedPercents.SequenceEqual([100, 125, 150, 175, 200, 225, 250, 300]),
    "Relative Windows DPI scale was not decoded into Settings percentages");
var paths = new[]
{
    new DisplayPath { Source = new Source { Id = 1, ModeIndex = 0 }, Flags = 1 },
    new DisplayPath { Source = new Source { Id = 2, ModeIndex = 1 }, Flags = 1 },
    new DisplayPath { Source = new Source { Id = 3, ModeIndex = 2 }, Flags = 1 }
};
var modes = new[]
{
    new Mode { Type = 1, X = 0, Y = 0 },
    new Mode { Type = 1, X = 2560, Y = 0 },
    new Mode { Type = 1, X = 4480, Y = -200 },
    new Mode { Type = 2, Data0 = 148500000, Data3 = 0x12345678 }
};
var (idlePaths, idleModes) = PrepareIdleTopology(paths, modes, [true, false, false], [true, false, false]);
Require(idlePaths.Length == 2 && idlePaths[0].Source.Id == 2, "Wrong display disconnected");
Require(idleModes[1].X == 0 && idleModes[2].X == 1920 && idleModes[2].Y == -200,
    "Physical monitor arrangement was not preserved");
Require(modes[1].X == 2560, "Original topology was mutated");
Require(idleModes[3].Data0 == modes[3].Data0 && idleModes[3].Data3 == modes[3].Data3,
    "Target signal timings were corrupted");
Reject(() => PrepareIdleTopology(paths, modes, [true, false, false], [false, true, false]));
Reject(() => PrepareIdleTopology([paths[0]], modes, [true], [true]));
Reject(() => PrepareIdleTopology(paths, modes, [true, true, true], [true, false, false]));
var absent = PrepareIdleTopology(paths, modes, [true, false, false], [false, false, false]);
Require(absent.Paths.Length == 3, "An inactive target must be a no-op");
var (exclusivePaths, exclusiveModes) = PrepareExclusiveVirtualTopology(paths, modes,
    [true, false, false], [true, false, false]);
Require(exclusivePaths.Length == 1 && exclusivePaths[0].Source.Id == 1,
    "Exclusive virtual mode must retain only the selected VDD");
Require(exclusiveModes[0].X == 0 && exclusiveModes[0].Y == 0,
    "Exclusive virtual desktop must begin at the Windows origin");
Require(exclusiveModes[3].Data0 == modes[3].Data0 && exclusiveModes[3].Data3 == modes[3].Data3,
    "Exclusive virtual mode corrupted target signal timings");
Require(IsExclusiveVirtualTopology(exclusivePaths, [true], [true]),
    "The selected VDD-only topology was not recognized as exclusive");
Require(!IsExclusiveVirtualTopology(paths, [true, false, false], [true, false, false]),
    "An extended physical plus virtual topology was incorrectly accepted as exclusive");
Require(!IsExclusiveVirtualTopology([paths[1]], [false], [true]),
    "A selected physical display was incorrectly accepted as a virtual-only desktop");
var inactiveVirtual = paths[0];
inactiveVirtual.Flags = 0;
inactiveVirtual.Source.ModeIndex = 42;
inactiveVirtual.Target.ModeIndex = 43;
var activatedVirtual = PrepareInactiveExclusiveVirtualTopology(
    [inactiveVirtual, paths[1]], [true, false], [true, false], [true, true]);
Require(activatedVirtual.Length == 1 && (activatedVirtual[0].Flags & 1) != 0 &&
    activatedVirtual[0].Source.ModeIndex == uint.MaxValue &&
    activatedVirtual[0].Target.ModeIndex == uint.MaxValue,
    "Detached VDD activation did not request a fresh Windows mode");
Reject(() => PrepareInactiveExclusiveVirtualTopology(
    [inactiveVirtual, paths[1]], [true, false], [false, true], [true, true]));
Reject(() => PrepareInactiveExclusiveVirtualTopology(
    [inactiveVirtual, paths[1]], [true, false], [true, false], [false, true]));
Reject(() => PrepareExclusiveVirtualTopology(paths, modes, [true, false, false], [false, true, false]));
Reject(() => PrepareExclusiveVirtualTopology(paths, modes, [true, false, false], [false, false, false]));
RejectArgument(() => IsExclusiveVirtualTopology(paths, [true], [true, false, false]));
var invalidPaths = (DisplayPath[])paths.Clone();
invalidPaths[1].Source.ModeIndex = uint.MaxValue;
invalidPaths[2].Source.ModeIndex = uint.MaxValue;
Reject(() => PrepareIdleTopology(invalidPaths, modes, [true, false, false], [true, false, false]));
Console.WriteLine("PASS topology guards, primary origin, multi-monitor layout, immutable modes, native ABI");

var bounds = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
var workArea = new System.Drawing.Rectangle(-1872, 40, 1872, 1040);
var normal = new WindowPlacementService.NativeRect(-1800, 100, -1000, 700);
var screenRect = WindowPlacementService.OffsetPlacement(normal, bounds, workArea, true, false);
Require(screenRect.Left == -1752 && screenRect.Top == 140, "Taskbar offsets were not applied");
var roundTrip = WindowPlacementService.OffsetPlacement(screenRect, bounds, workArea, true, true);
Require(roundTrip.Equals(normal), "Window placement did not round-trip");
Require(WindowPlacementService.OffsetPlacement(normal, bounds, workArea, false, false).Equals(normal),
    "Tool window screen coordinates must not be offset");
var smallArea = new System.Drawing.Rectangle(0, 0, 640, 480);
var clamped = WindowPlacementService.TranslateAndClamp(screenRect, workArea, smallArea);
Require(clamped.Left == 0 && clamped.Top == 0 && clamped.Right == 640 && clamped.Bottom == 480,
    "Oversized window was not clamped to the target work area");
WindowPlacementService.ResetSessionDesktop();
WindowPlacementService.ObserveSessionDesktop(false);
Require(WindowPlacementService.SessionRestorePending, "Display transition did not freeze capture");
WindowPlacementService.ObserveSessionDesktop(true);
Require(WindowPlacementService.SessionRestorePending, "Recovery must wait for the settling observation");
WindowPlacementService.ResetSessionDesktop();
Require(!WindowPlacementService.SessionRestorePending, "Disabling recovery retained stale state");
Console.WriteLine("PASS taskbar coordinates, negative monitor origin, tool windows, clamping, recovery settling");

var originalWindow = new WindowPlacementService.NativeRect(100, 100, 1300, 1000);
var largeArea = new System.Drawing.Rectangle(0, 0, 2560, 1440);
var streamArea = new System.Drawing.Rectangle(0, 0, 1920, 1040);
var scaled = WindowPlacementService.ScaleAndClamp(originalWindow, largeArea, streamArea, 144, 96);
Require(scaled.Right - scaled.Left == 800 && scaled.Bottom - scaled.Top == 600,
    "Virtual desktop must retain the original logical window size at its DPI");
var fitted = WindowPlacementService.ScaleAndClamp(originalWindow, largeArea, smallArea, 96, 144);
Require(fitted.Right == 640 && fitted.Bottom == 480 && fitted.Left == 0 && fitted.Top == 0,
    "DPI adaptation must fit the virtual work area");
var unchanged = WindowPlacementService.ScaleAndClamp(originalWindow, largeArea, streamArea, 96, 96);
Require(unchanged.Equals(originalWindow), "Same-DPI connection must not shrink a fitting window");
Require(WindowPlacementService.PlacementMatches(originalWindow,
    new WindowPlacementService.NativeRect(102, 98, 1301, 1002), 3),
    "Restore verification must tolerate harmless window-manager rounding");
Require(!WindowPlacementService.PlacementMatches(originalWindow,
    new WindowPlacementService.NativeRect(100, 100, 1200, 900), 3),
    "Restore verification must detect an overwritten window size");
Console.WriteLine("PASS logical window size, same-DPI stability and virtual work-area fitting");

// Read-only native smoke check: never apply a topology in automated tests.
var tracker = new DesktopRecoveryTracker();
tracker.CaptureBaseline("two-monitors");
tracker.BeginTransition();
tracker.Observe(true, "two-monitors", 0);
Require(tracker.Observe(true, "two-monitors", 3000) == DesktopRecoveryAction.Restore,
    "An explicit client transition must preserve an immediate baseline");
tracker.Reset();
Require(tracker.Observe(true, "two-monitors", 0) == DesktopRecoveryAction.Wait, "Initial capture must settle");
Require(tracker.Observe(true, "two-monitors", 2999) == DesktopRecoveryAction.Wait, "Rapid ticks cannot settle early");
Require(tracker.Observe(true, "two-monitors", 3000) == DesktopRecoveryAction.Capture, "Stable idle layout must capture");
Require(tracker.Observe(false, "virtual", 4000) == DesktopRecoveryAction.Wait, "Streaming must freeze snapshot");
Require(tracker.Observe(true, "one-monitor", 5000) == DesktopRecoveryAction.Wait, "First physical monitor is insufficient");
Require(tracker.Observe(true, "one-monitor", 9000) == DesktopRecoveryAction.Wait, "Delayed monitor must retain original snapshot");
Require(tracker.Observe(true, "two-monitors", 10000) == DesktopRecoveryAction.Wait, "Restored layout must settle again");
Require(tracker.Observe(true, "two-monitors", 13000) == DesktopRecoveryAction.Restore, "Both monitors must restore the baseline");
Require(!tracker.Pending && !tracker.Waiting, "Successful restore must release recovery");
Require(tracker.Observe(true, "two-monitors", 13001) == DesktopRecoveryAction.Wait,
    "Async window restoration must settle before the next snapshot");
Require(tracker.Observe(true, "two-monitors", 16001) == DesktopRecoveryAction.Capture,
    "Restored window sizes must become the next idle baseline after settling");
tracker.Reset();
tracker.Observe(true, "original", 0);
tracker.Observe(true, "original", 3000);
tracker.BeginTransition();
tracker.Observe(true, "unplugged", 5000);
Require(tracker.Observe(true, "unplugged", 19999) == DesktopRecoveryAction.Wait, "Unplug grace period ended early");
Require(tracker.Observe(true, "unplugged", 20000) == DesktopRecoveryAction.Discard, "Never restore coordinates for a removed display");
tracker.Reset();
tracker.Observe(true, "original", 0);
tracker.Observe(true, "original", 3000);
tracker.BeginTransition();
tracker.Observe(true, "original", 5000);
tracker.Observe(false, "virtual", 7900);
tracker.Observe(true, "original", 8000);
Require(tracker.Observe(true, "original", 8100) == DesktopRecoveryAction.Wait, "Reconnect must restart settling");
Require(tracker.Observe(true, "original", 11000) == DesktopRecoveryAction.Restore, "Reconnect lost baseline");
tracker.Reset();
tracker.BeginTransition();
tracker.Observe(true, "original", 0);
Require(tracker.Observe(true, "original", 3000) == DesktopRecoveryAction.Discard, "Startup during streaming cannot invent a snapshot");
Console.WriteLine("PASS delayed monitors, stable capture, unplug grace, fast reconnect, no-baseline recovery");

tracker.Reset();
Require(tracker.CaptureBaseline("physical-original"), "Initial client preparation must capture");
tracker.BeginTransition();
Require(!tracker.CaptureBaseline("temporary-small-desktop"),
    "Rapid reconnect must not overwrite the original window baseline");
tracker.BeginTransition();
tracker.Observe(true, "physical-original", 0);
Require(tracker.Observe(true, "physical-original", 3000) == DesktopRecoveryAction.Restore,
    "Changing client mode must retain recovery for the original desktop");
Require(tracker.CaptureBaseline("new-idle-layout"), "Completed recovery must allow a new baseline");
Console.WriteLine("PASS client preparation preserves the baseline across reconnect and mode changes");

var actual = ReadActive();
Require(actual.Paths.Length > 0, "Windows did not return an active desktop");
foreach (var path in actual.Paths)
    Require(path.Source.ModeIndex < actual.Modes.Length && actual.Modes[path.Source.ModeIndex].Type == 1,
        "Native source mode did not round-trip");
Console.WriteLine($"PASS native QueryDisplayConfig: {actual.Paths.Length} active paths; physical-only={IsPhysicalDesktopOnly()}");
Require(!string.IsNullOrEmpty(ReadDesktopState().Layout), "Native topology fingerprint is empty");
foreach (var screen in System.Windows.Forms.Screen.AllScreens)
{
    var scale = DisplayTopologyService.TryGetDisplayScale(screen.DeviceName);
    Require(scale is not null && scale.SupportedPercents.Contains(scale.CurrentPercent),
        $"Windows display scale was not readable for {screen.DeviceName}");
}
Console.WriteLine("PASS native per-monitor Windows scale inventory (read-only)");
var primarySource = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
var primaryKey = CaptureOutputKey(primarySource, null);
if (primaryKey != null) {
    var sessionOutputs = SessionOutputs(primaryKey);
    Require(sessionOutputs.Count(o => o.Capture && o.Active) == 1, "Native capture must resolve uniquely.");
    foreach (var secondaryOutput in sessionOutputs.Where(o => !o.Capture && o.Active)) {
        SetSessionOutput(primaryKey, secondaryOutput.Key, false, validateOnly: true);
        Console.WriteLine($"PASS Windows validated disabling secondary output {secondaryOutput.Name} without applying it");
    }
    Require(SessionOutputs(primaryKey).SequenceEqual(sessionOutputs), "Validation changed the desktop");
}

var physicalRecovery = new PhysicalDisplayRecoveryPolicy();
physicalRecovery.CaptureBaseline("left+main+right");
physicalRecovery.BeginPreparation(45000);
Require(physicalRecovery.Observe(false, false, "virtual", 45000) == PhysicalDisplayAction.Wait,
    "A prepared virtual desktop must enter disconnect recovery");
Require(physicalRecovery.Observe(false, false, "virtual", 50000) == PhysicalDisplayAction.Restore,
    "A prepared VDD with no client must restore the captured physical desktop");
physicalRecovery = new PhysicalDisplayRecoveryPolicy();
physicalRecovery.BeginPreparation(0);
Require(physicalRecovery.Observe(false, true, "left+main+right", 0) == PhysicalDisplayAction.Wait, "Capture must settle");
Require(physicalRecovery.Observe(false, true, "left+main+right", 3000) == PhysicalDisplayAction.Capture, "Missing multi-screen baseline");
Require(physicalRecovery.Observe(true, true, "main", 4000) == PhysicalDisplayAction.Wait, "Do not restore during exclusive physical stream");
Require(physicalRecovery.Observe(false, true, "main", 5000) == PhysicalDisplayAction.Wait, "Missing disconnect grace");
Require(physicalRecovery.Observe(false, true, "main", 10000) == PhysicalDisplayAction.Restore, "Single physical monitor must restore the entire layout");
Require(physicalRecovery.Observe(true, false, "virtual", 11000) == PhysicalDisplayAction.Wait, "Reconnection must cancel recovery");
Require(physicalRecovery.Observe(false, false, "virtual", 12000) == PhysicalDisplayAction.Wait, "Virtual stream needs fresh grace");
Require(physicalRecovery.Observe(false, false, "virtual", 17000) == PhysicalDisplayAction.Restore, "Virtual-only desktop must recover");
Require(physicalRecovery.Observe(false, true, "left+main+right", 18000) == PhysicalDisplayAction.Wait, "Already restored layout must not be applied again");
Console.WriteLine("PASS complete physical topology baseline, partial restore, virtual-only restore and rapid reconnect guards");

Require(!PhysicalDisplayRecoveryPolicy.ShouldRestore(false, true, false, false, true), "Unowned virtual desktop must never be restored");
Require(!PhysicalDisplayRecoveryPolicy.ShouldRestore(false, true, true, true, false), "Preparing lease must retain its full grace period");
Require(!PhysicalDisplayRecoveryPolicy.ShouldRestore(null, true, true, false, true), "API failure with a live engine is Unknown, not zero streams");
Require(PhysicalDisplayRecoveryPolicy.ShouldRestore(null, false, true, false, true), "A dead engine must allow independent desktop recovery");
Require(!PhysicalDisplayRecoveryPolicy.ShouldRestore(false, true, true, false, true, true, TimeSpan.FromSeconds(2)),
    "A brief zero-stream sample during encoder reconfiguration must not restore the desktop");
Require(PhysicalDisplayRecoveryPolicy.ShouldRestore(false, true, true, false, true, true, TimeSpan.FromSeconds(5)),
    "A continuously disconnected stream must restore after the grace period");
Require(!PhysicalDisplayRecoveryPolicy.ShouldRestore(null, true, true, false, true, true, TimeSpan.FromSeconds(10)),
    "An unknown API state must not authorize display restoration");
Require(PhysicalDisplayRecoveryPolicy.CanRestoreReleasedSession("Aborted", false, false),
    "An explicitly released idle session can restore its desktop immediately");
Require(!PhysicalDisplayRecoveryPolicy.CanRestoreReleasedSession("Aborted", false, true) &&
        !PhysicalDisplayRecoveryPolicy.CanRestoreReleasedSession("Aborted", true, false) &&
        !PhysicalDisplayRecoveryPolicy.CanRestoreReleasedSession("Aborted", null, false),
    "A retained application, an active stream or unknown engine state must keep the normal recovery path");
Require(new[] { "Streaming", "Ready", "Preparing", "AwaitingApplication", "Fault" }
        .All(phase => !PhysicalDisplayRecoveryPolicy.CanRestoreReleasedSession(phase, false, false)),
    "Quiet samples without an explicit release must keep the reconnect grace period");
internal static class DpiProbe
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}

