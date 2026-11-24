using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PiuLink.Host.Services;

/// <summary>
/// Disconnects a streaming display from the desktop without disabling its PnP device.
/// Sunshine can then activate the same stable device ID and restore this idle topology.
/// Never removes the last display or an unverified physical display.
/// </summary>
internal static partial class DisplayTopologyService
{
    private const uint AllPaths = 1, ActivePaths = 2;
    private const uint PathActive = 1, InvalidModeIndex = uint.MaxValue;
    private const uint Supplied = 0x20, Validate = 0x40, Apply = 0x80, NoOptimization = 0x100,
        AllowChanges = 0x400,
        ForceModeEnumeration = 0x1000;
    private static readonly int[] DpiScalePercents =
        [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    internal sealed record Snapshot(DisplayPath[] Paths, Mode[] Modes, string[] TargetNames)
    {
        public Dictionary<string, PiuLink.Host.Core.Sunshine.DesktopRectangle> StreamBounds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string Layout { get; init; } = "";
        public Dictionary<string, int> Scales { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
    internal sealed record DisplayMode(int Width, int Height, int Fps);
    internal sealed record DisplayScale(int CurrentPercent, int RecommendedPercent, int[] SupportedPercents);
    internal sealed record ActiveVirtualDisplay(
        string DevicePath,
        string DisplayName,
        string FriendlyName,
        string AdapterName,
        string AdapterDeviceId);
    internal static IReadOnlyList<int> SupportedScalePercents => DpiScalePercents;
    public static string[] GetActiveVirtualTargets() => GetActiveVirtualDisplays()
        .Select(display => display.DevicePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static ActiveVirtualDisplay[] GetActiveVirtualDisplays() => ReadActive().Paths
        .Select(path =>
        {
            var target = GetTargetName(path);
            var displayName = GetSourceName(path);
            var adapter = GetDisplayAdapter(displayName);
            return new ActiveVirtualDisplay(
                target.DevicePath,
                displayName,
                target.FriendlyName,
                adapter.DeviceString,
                adapter.DeviceId);
        })
        .Where(display => IsVirtualIdentity(
            display.FriendlyName,
            display.AdapterName,
            display.AdapterDeviceId))
        .ToArray();

    public static Snapshot? CapturePhysicalDesktop()
    {
        var (paths, modes) = ReadActive();
        if (paths.Length == 0 || paths.Any(IsVirtual)) return null;
        return CaptureSnapshot(paths, modes);
    }

    public static Snapshot CaptureDesktop() {
        var (paths, modes) = ReadActive();
        if (paths.Any(path => string.IsNullOrWhiteSpace(GetTargetName(path).DevicePath) ||
            string.IsNullOrWhiteSpace(GetDisplayAdapter(GetSourceName(path)).DeviceString)))
            throw new InvalidOperationException("Windows display identity is incomplete; retry after the topology settles.");
        return CaptureSnapshot(paths, modes);
    }

    private static Snapshot CaptureSnapshot(DisplayPath[] paths, Mode[] modes) =>
        new(paths, modes, paths.Select(path => GetTargetName(path).DevicePath).ToArray()) {
            Layout = LayoutSignature(paths, modes),
            Scales = paths.Select(path => (Name: GetTargetName(path).DevicePath, Scale: TryGetDisplayScale(GetSourceName(path))))
                .Where(item => item.Scale != null).DistinctBy(item => item.Name)
                .ToDictionary(item => item.Name, item => item.Scale!.CurrentPercent, StringComparer.OrdinalIgnoreCase)
        };

    public static bool RestorePhysicalDesktop(Snapshot snapshot)
    {
        var (available, _) = ReadTopology(AllPaths); // QDC_ALL_PATHS includes connected but disabled monitors.
        var names = available.Where(path => path.Target.Available != 0)
            .Select(path => GetTargetName(path).DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = snapshot.Paths.Where((path, index) => names.Contains(snapshot.TargetNames[index])).ToArray();
        if (paths.Length == 0) {
            // Another display driver can remove every saved output before recovery.
            // Keep a verified replacement physical desktop; the journal will retain
            // the original snapshot as PartialRecovered instead of blocking forever.
            var replacement = CaptureDesktop();
            if (replacement.Paths.Length > 0 && replacement.Paths.All(path => !IsVirtual(path))) return true;
            foreach (var candidate in available.Where(p => p.Target.Available != 0 && !IsVirtual(p))
                .DistinctBy(p => GetTargetName(p).DevicePath, StringComparer.OrdinalIgnoreCase))
            {
                var fallback = candidate;
                fallback.Source.ModeIndex = InvalidModeIndex;
                fallback.Target.ModeIndex = InvalidModeIndex;
                fallback.Flags |= PathActive;
                DisplayPath[] route = [fallback];
                if (SetDisplayConfig(1, route, 0, null!, Supplied | Validate | AllowChanges) == 0 &&
                    SetDisplayConfig(1, route, 0, null!, Supplied | Apply | AllowChanges | ForceModeEnumeration) == 0)
                {
                    replacement = CaptureDesktop();
                    if (replacement.Paths.Length > 0 && replacement.Paths.All(path => !IsVirtual(path))) return true;
                }
            }
            return false;
        }
        var modeList = snapshot.Modes.ToList();
        var claimedSourceModes = new Dictionary<uint, (Luid Adapter, uint Id)>();
        var originalPaths = (DisplayPath[])paths.Clone();
        for (var index = 0; index < paths.Length; index++) {
            var originalIndex = Array.FindIndex(snapshot.Paths, path => path.Equals(paths[index]));
            var name = snapshot.TargetNames[originalIndex];
            var saved = originalPaths[index];
            var candidates = available.Where(path => path.Target.Available != 0 &&
                string.Equals(GetTargetName(path).DevicePath, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0) return false;
            bool SameSource(DisplayPath left, DisplayPath right) =>
                left.Source.Id == right.Source.Id && left.Source.Adapter.Equals(right.Source.Adapter);
            var rebound = candidates.FirstOrDefault(path => SameSource(path, saved));
            if (rebound.Equals(default(DisplayPath))) {
                // Do not turn two different saved desktops into clones when PnP renumbers sources.
                rebound = candidates.OrderByDescending(path => (path.Flags & PathActive) != 0)
                    .FirstOrDefault(path => !paths.Take(index).Any(used => SameSource(used, path)) &&
                        !originalPaths.Skip(index + 1).Any(other => SameSource(other, path)));
                if (rebound.Equals(default(DisplayPath))) {
                    rebound = candidates.OrderByDescending(path => (path.Flags & PathActive) != 0)
                        .FirstOrDefault(path => !paths.Take(index).Any(used => SameSource(used, path)));
                }
                if (rebound.Equals(default(DisplayPath))) rebound = candidates[0];
            }
            paths[index].Source.Adapter = rebound.Source.Adapter;
            paths[index].Source.Id = rebound.Source.Id;
            paths[index].Target.Adapter = rebound.Target.Adapter;
            paths[index].Target.Id = rebound.Target.Id;
            // Keep the saved source mode, including its virtual-desktop (x,y), on the new path.
            if (saved.Source.ModeIndex < modeList.Count) {
                var sourceModeIndex = saved.Source.ModeIndex;
                var owner = (rebound.Source.Adapter, rebound.Source.Id);
                if (claimedSourceModes.TryGetValue(sourceModeIndex, out var existingOwner) && !existingOwner.Equals(owner)) {
                    var clonedMode = modeList[(int)sourceModeIndex];
                    clonedMode.Adapter = rebound.Source.Adapter;
                    clonedMode.Id = rebound.Source.Id;
                    paths[index].Source.ModeIndex = (uint)modeList.Count;
                    modeList.Add(clonedMode);
                } else {
                    var updatedMode = modeList[(int)sourceModeIndex];
                    updatedMode.Adapter = rebound.Source.Adapter;
                    updatedMode.Id = rebound.Source.Id;
                    modeList[(int)sourceModeIndex] = updatedMode;
                    claimedSourceModes[sourceModeIndex] = owner;
                }
            }
            if (saved.Target.ModeIndex < modeList.Count) {
                var targetMode = modeList[(int)saved.Target.ModeIndex];
                targetMode.Adapter = rebound.Target.Adapter;
                targetMode.Id = rebound.Target.Id;
                modeList[(int)saved.Target.ModeIndex] = targetMode;
            }
        }
        var modes = modeList.ToArray();
        if (paths.Length < snapshot.Paths.Length) {
            var origin = paths.FirstOrDefault(path =>
                path.Source.ModeIndex < modes.Length &&
                modes[path.Source.ModeIndex].X == 0 &&
                modes[path.Source.ModeIndex].Y == 0);
            if (origin.Equals(default(DisplayPath))) {
                origin = paths.FirstOrDefault(path => path.Source.ModeIndex < modes.Length);
            }
            if (origin.Source.ModeIndex < modes.Length) {
                var x = modes[origin.Source.ModeIndex].X; var y = modes[origin.Source.ModeIndex].Y;
                foreach (var modeIndex in paths.Select(path => path.Source.ModeIndex).Distinct().Where(index => index < modes.Length)) {
                    modes[modeIndex].X -= x; modes[modeIndex].Y -= y;
                }
            }
        }
        Check(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, Supplied | Validate | AllowChanges));
        // Re-drive every restored path even when Windows' topology cache already claims it is
        // active. Without this, some monitors remain black after an exclusive VDD session.
        Check(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            Supplied | Apply | NoOptimization | AllowChanges | ForceModeEnumeration));
        var actual = ReadActive();
        foreach (var path in actual.Paths) {
            if (snapshot.Scales.TryGetValue(GetTargetName(path).DevicePath, out var scale) &&
                !SetDisplayScale(GetSourceName(path), scale)) return false;
        }
        if (snapshot.TargetNames.All(names.Contains))
            return LayoutMatches(actual.Paths, actual.Modes, snapshot);
        // A missing monitor must not prevent restoring the still-connected output.
        return actual.Paths.Length > 0 && actual.Paths.All(path => snapshot.TargetNames.Contains(
            GetTargetName(path).DevicePath, StringComparer.OrdinalIgnoreCase));
    }

    private static bool LayoutMatches(DisplayPath[] actualPaths, Mode[] actualModes, Snapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.Layout)) return true;
        var actualLayout = LayoutSignature(actualPaths, actualModes);
        if (actualLayout == snapshot.Layout) return true;
        var actualEntries = actualLayout.Split('\n');
        var expectedEntries = snapshot.Layout.Split('\n');
        if (actualEntries.Length != expectedEntries.Length) return false;
        for (var i = 0; i < actualEntries.Length; i++) {
            var a = actualEntries[i].Split('|');
            var e = expectedEntries[i].Split('|');
            if (a.Length != 5 || e.Length != 5) return false;
            if (!string.Equals(a[0], e[0], StringComparison.Ordinal) ||
                !string.Equals(a[1], e[1], StringComparison.Ordinal) ||
                !string.Equals(a[2], e[2], StringComparison.Ordinal) ||
                !string.Equals(a[3], e[3], StringComparison.Ordinal)) return false;
            if (!TryParseRefreshHz(a[4], out var actualHz) || !TryParseRefreshHz(e[4], out var expectedHz) ||
                Math.Abs(actualHz - expectedHz) > 1.0) return false;
        }
        return true;
    }

    private static bool TryParseRefreshHz(string rational, out double hz)
    {
        hz = 0;
        var slash = rational.IndexOf('/');
        if (slash <= 0 || slash >= rational.Length - 1) return false;
        if (!double.TryParse(rational.AsSpan(0, slash), System.Globalization.CultureInfo.InvariantCulture, out var num) ||
            !double.TryParse(rational.AsSpan(slash + 1), System.Globalization.CultureInfo.InvariantCulture, out var den) ||
            den <= 0) return false;
        hz = num / den;
        return true;
    }

    internal static bool AllSnapshotTargetsConnected(Snapshot snapshot)
    {
        var names = ReadTopology(AllPaths).Item1.Where(path => path.Target.Available != 0)
            .Select(path => GetTargetName(path).DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.TargetNames.Length > 0 && snapshot.TargetNames.All(names.Contains);
    }

    public static bool AllSnapshotTargetsActive(Snapshot snapshot)
    {
        var names = ReadActive().Paths.Select(path => GetTargetName(path).DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.TargetNames.All(names.Contains);
    }

    // Explicit repair for a desktop that was already broken before PiuLink started.
    // Windows retrieves its last extended configuration; never invoke this blindly during a stream.
    public static void RestoreLastExtendedDesktop() =>
        Check(SetDisplayConfig(0, null!, 0, null!, Apply | 0x4));

    public static bool IsPhysicalDesktopOnly()
        => ReadDesktopState().PhysicalOnly;

    public static (bool PhysicalOnly, string Layout) ReadDesktopState()
    {
        var (paths, modes) = ReadActive();
        return (paths.Length > 0 && paths.All(path => !IsVirtual(path)), LayoutSignature(paths, modes));
    }

    private static string LayoutSignature(DisplayPath[] paths, Mode[] modes)
    {
        var entries = paths.Select(path =>
        {
            if (path.Source.ModeIndex >= modes.Length || modes[path.Source.ModeIndex].Type != 1)
                throw new InvalidOperationException("The display layout is still changing.");
            var mode = modes[path.Source.ModeIndex];
            var target = GetTargetName(path);
            return (Virtual: IsVirtual(path), Key:
                $"{target.DevicePath.ToUpperInvariant()}|{mode.X},{mode.Y}|{mode.Data0}|{path.Target.Rotation}|{path.Target.RefreshNumerator}/{path.Target.RefreshDenominator}");
        }).ToArray();
        return string.Join("\n", entries.Select(entry => entry.Key).Order(StringComparer.Ordinal));
    }

    public static string? FindActiveVirtualTarget(string displayName, string? friendlyName = null)
    {
        var (paths, _) = ReadActive();
        var selected = paths.Where(path => MatchesRequestedDisplay(
            GetSourceName(path), GetTargetName(path).FriendlyName, displayName, friendlyName)).ToArray();
        if (selected.Length == 0) return null;
        if (selected.Length != 1 || !IsVirtual(selected[0]))
            throw new InvalidOperationException("Select an extended virtual display before preparing the idle desktop.");
        var devicePath = GetTargetName(selected[0]).DevicePath;
        if (string.IsNullOrWhiteSpace(devicePath))
            throw new InvalidOperationException("Windows did not return a stable virtual display identity.");
        return devicePath;
    }

    /// <summary>Returns the Windows source name for the sole active virtual desktop.</summary>
    public static string? GetSingleActiveVirtualDisplayName()
    {
        var (paths, _) = ReadActive();
        var virtualPaths = paths.Where(IsVirtual).ToArray();
        return virtualPaths.Length == 1 ? GetSourceName(virtualPaths[0]) : null;
    }

    public static string? FindActiveVirtualDisplayName(string displayName, string? friendlyName = null)
    {
        var (paths, _) = ReadActive();
        var selected = paths.Where(path => IsVirtual(path) && MatchesRequestedDisplay(
            GetSourceName(path), GetTargetName(path).FriendlyName, displayName, friendlyName)).ToArray();
        return selected.Length == 1 ? GetSourceName(selected[0]) : null;
    }

    public static bool DisconnectVirtualDesktop(string devicePath)
    {
        var (paths, modes) = ReadActive();
        var virtualPaths = paths.Select(IsVirtual).ToArray();
        var selectedPaths = paths.Select(path =>
            string.Equals(GetTargetName(path).DevicePath, devicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var (remaining, adjustedModes) = PrepareIdleTopology(paths, modes, virtualPaths, selectedPaths);
        if (remaining.Length == paths.Length)
        {
            return false;
        }

        Check(SetDisplayConfig((uint)remaining.Length, remaining, (uint)adjustedModes.Length,
            adjustedModes, Supplied | Validate | AllowChanges));
        Check(SetDisplayConfig((uint)remaining.Length, remaining, (uint)adjustedModes.Length,
            adjustedModes, Supplied | Apply | AllowChanges));
        // Deliberately do not disable the driver or overwrite the Windows persistence database.
        return true;
    }

    /// <summary>
    /// Makes the named virtual output the only active Windows desktop. This deliberately
    /// disables every physical path without disabling its PnP device, so the saved physical
    /// topology can be restored after the streaming session ends.
    /// </summary>
    public static bool UseVirtualDesktopOnly(string displayName, string? friendlyName = null)
    {
        var (paths, modes) = ReadActive();
        var virtualPaths = paths.Select(IsVirtual).ToArray();
        var selectedPaths = paths.Select(path => MatchesRequestedDisplay(
                GetSourceName(path), GetTargetName(path).FriendlyName, displayName, friendlyName))
            .ToArray();
        // Sunshine can report its route before Windows has attached the selected VDD.
        // Never substitute another vendor's sole active virtual display: doing so can make
        // the encoder capture an output that disappears when that vendor changes topology.
        if (!selectedPaths.Any(selected => selected)) return false;
        var (retained, adjustedModes) = PrepareExclusiveVirtualTopology(paths, modes, virtualPaths, selectedPaths);
        if (retained.Length == paths.Length) return false;
        Check(SetDisplayConfig((uint)retained.Length, retained, (uint)adjustedModes.Length,
            adjustedModes, Supplied | Validate | AllowChanges));
        Check(SetDisplayConfig((uint)retained.Length, retained, (uint)adjustedModes.Length,
            adjustedModes, Supplied | Apply | AllowChanges));
        return true;
    }

    public static bool IsOnlyActiveSource(string displayName)
    {
        var paths = ReadActive().Paths;
        return paths.Length == 1 && !IsVirtual(paths[0]) && DisplayNamesEqual(GetSourceName(paths[0]), displayName);
    }

    public static void UsePhysicalDisplayOnly(string displayName)
    {
        var (paths, modes) = ReadActive();
        var matches = paths.Where(path => DisplayNamesEqual(GetSourceName(path), displayName) && !IsVirtual(path)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The physical display is unavailable or ambiguous.");
        if (paths.Length == 1) return;
        var selected = matches[0];
        if (selected.Source.ModeIndex >= modes.Length) throw new InvalidOperationException("Physical display mode is unknown.");
        modes[selected.Source.ModeIndex].X = 0; modes[selected.Source.ModeIndex].Y = 0;
        Check(SetDisplayConfig(1, matches, (uint)modes.Length, modes, Supplied | Validate | AllowChanges));
        Check(SetDisplayConfig(1, matches, (uint)modes.Length, modes, Supplied | Apply | AllowChanges));
        var actual = ReadActive().Paths;
        if (actual.Length != 1 || GetTargetName(actual[0]).DevicePath != GetTargetName(selected).DevicePath)
            throw new InvalidOperationException("Windows did not confirm the selected physical display.");
    }

    /// <summary>
    /// Activates an available but detached VDD as the sole desktop path. Sunshine otherwise
    /// silently falls back to a physical output when its configured virtual target is inactive.
    /// Windows chooses a supported source and target mode; Sunshine can then apply the exact
    /// client-requested mode before capture begins.
    /// </summary>
    public static bool ActivateVirtualDesktopOnly(string displayName, string? friendlyName = null)
    {
        if (IsVirtualDesktopOnly(displayName, friendlyName)) return false;
        var (paths, _) = ReadTopology(AllPaths);
        var virtualPaths = paths.Select(IsVirtual).ToArray();
        var selectedPaths = paths.Select(path => MatchesRequestedDisplay(
                GetSourceName(path), GetTargetName(path).FriendlyName, displayName, friendlyName))
            .ToArray();
        var availablePaths = paths.Select(path => path.Target.Available != 0).ToArray();
        var targetGroups = paths.Select((path, index) => (path, index))
            .Where(item => selectedPaths[item.index] && availablePaths[item.index] && virtualPaths[item.index])
            .GroupBy(item => (item.path.Target.Adapter.Low, item.path.Target.Adapter.High, item.path.Target.Id)).ToArray();
        if (targetGroups.Length != 1) throw new InvalidOperationException("The selected virtual target is ambiguous or missing.");
        DisplayPath[]? activated = null;
        foreach (var candidate in targetGroups[0].OrderByDescending(item => (item.path.Flags & PathActive) != 0)) {
            var path = candidate.path; path.Flags |= PathActive;
            path.Source.ModeIndex = InvalidModeIndex; path.Target.ModeIndex = InvalidModeIndex;
            DisplayPath[] route = [path];
            if (SetDisplayConfig(1, route, 0, null!, Supplied | Validate | AllowChanges) == 0) { activated = route; break; }
        }
        if (activated is null) throw new InvalidOperationException("No valid Windows route exists for this virtual target.");
        // SetDisplayConfig requires a NULL mode-info pointer when the count is zero.
        // Array.Empty<T>() can marshal as a non-NULL pointer and is rejected with
        // ERROR_INVALID_PARAMETER before best-mode selection runs.
        Check(SetDisplayConfig((uint)activated.Length, activated, 0, null!,
            Supplied | Validate | AllowChanges));
        Check(SetDisplayConfig((uint)activated.Length, activated, 0, null!,
            Supplied | Apply | AllowChanges | ForceModeEnumeration));
        return true;
    }

    /// <summary>Returns true only when the selected VDD is the sole active Windows desktop path.</summary>
    public static bool IsVirtualDesktopOnly(string displayName, string? friendlyName = null)
    {
        var (paths, _) = ReadActive();
        var virtualPaths = paths.Select(IsVirtual).ToArray();
        var selectedPaths = paths.Select(path => MatchesRequestedDisplay(
                GetSourceName(path), GetTargetName(path).FriendlyName, displayName, friendlyName))
            .ToArray();
        return IsExclusiveVirtualTopology(paths, virtualPaths, selectedPaths);
    }

    internal static bool IsExclusiveVirtualTopology(
        DisplayPath[] paths, bool[] virtualPaths, bool[] selectedPaths)
    {
        if (paths.Length != virtualPaths.Length || paths.Length != selectedPaths.Length)
            throw new ArgumentException("Display classification does not match the topology.");
        return paths.Length == 1 && virtualPaths[0] && selectedPaths[0];
    }

    internal static DisplayPath[] PrepareInactiveExclusiveVirtualTopology(
        DisplayPath[] paths, bool[] virtualPaths, bool[] selectedPaths, bool[] availablePaths)
    {
        if (paths.Length != virtualPaths.Length || paths.Length != selectedPaths.Length ||
            paths.Length != availablePaths.Length)
            throw new ArgumentException("Display classification does not match the topology.");
        var selectedIndices = Enumerable.Range(0, paths.Length)
            .Where(index => selectedPaths[index] && availablePaths[index])
            .ToArray();
        if (selectedIndices.Length == 0 || selectedIndices.Any(index => !virtualPaths[index]) ||
            selectedIndices.Select(index => (paths[index].Target.Adapter.Low, paths[index].Target.Adapter.High,
                paths[index].Target.Id)).Distinct().Count() != 1)
            throw new InvalidOperationException("The selected virtual display is not uniquely available.");

        var selected = paths[selectedIndices[0]];
        selected.Flags |= PathActive;
        selected.Source.ModeIndex = InvalidModeIndex;
        selected.Target.ModeIndex = InvalidModeIndex;
        return [selected];
    }

    internal static (DisplayPath[] Paths, Mode[] Modes) PrepareExclusiveVirtualTopology(
        DisplayPath[] paths, Mode[] modes, bool[] virtualPaths, bool[] selectedPaths)
    {
        if (paths.Length != virtualPaths.Length || paths.Length != selectedPaths.Length)
            throw new ArgumentException("Display classification does not match the topology.");
        var selectedIndices = Enumerable.Range(0, paths.Length).Where(index => selectedPaths[index]).ToArray();
        if (selectedIndices.Length != 1 || !virtualPaths[selectedIndices[0]])
            throw new InvalidOperationException("The exclusive display is not an active virtual display.");

        var selected = paths[selectedIndices[0]];
        if (selected.Source.ModeIndex >= modes.Length || modes[selected.Source.ModeIndex].Type != 1)
            throw new InvalidOperationException("Windows returned an incomplete virtual desktop mode.");
        var adjustedModes = (Mode[])modes.Clone();
        var origin = modes[selected.Source.ModeIndex];
        adjustedModes[selected.Source.ModeIndex].X -= origin.X;
        adjustedModes[selected.Source.ModeIndex].Y -= origin.Y;
        return (new[] { selected }, adjustedModes);
    }

    internal static (DisplayPath[] Paths, Mode[] Modes) PrepareIdleTopology(
        DisplayPath[] paths, Mode[] modes, bool[] virtualPaths, bool[] selectedPaths)
    {
        if (paths.Length != virtualPaths.Length || paths.Length != selectedPaths.Length)
            throw new ArgumentException("Display classification does not match the topology.");
        if (selectedPaths.Where((selected, index) => selected && !virtualPaths[index]).Any())
            throw new InvalidOperationException("The selected display is not a verified virtual display.");
        if (!selectedPaths.Any(selected => selected)) return (paths, modes);
        var retainedIndices = Enumerable.Range(0, paths.Length).Where(index => !selectedPaths[index]).ToArray();
        var remaining = retainedIndices.Select(index => paths[index]).ToArray();
        var physical = retainedIndices.Where(index => !virtualPaths[index]).Select(index => paths[index]).ToArray();
        if (physical.Length == 0)
        {
            throw new InvalidOperationException("A physical display must be active before the virtual desktop can be disconnected.");
        }

        var adjustedModes = (Mode[])modes.Clone();
        // The retained desktop must still have an origin, even if the VDD was primary.
        var primary = physical.FirstOrDefault(path =>
            path.Source.ModeIndex < modes.Length && modes[path.Source.ModeIndex].X == 0 &&
            modes[path.Source.ModeIndex].Y == 0);
        if (primary.Equals(default(DisplayPath))) primary = physical[0];
        if (primary.Source.ModeIndex >= modes.Length || modes[primary.Source.ModeIndex].Type != 1)
            throw new InvalidOperationException("Windows returned an incomplete primary desktop mode.");
        var origin = modes[primary.Source.ModeIndex];
        foreach (var index in remaining.Select(path => path.Source.ModeIndex).Distinct())
        {
            if (index >= adjustedModes.Length || adjustedModes[index].Type != 1)
            {
                throw new InvalidOperationException("Windows returned an incomplete desktop mode.");
            }
            adjustedModes[index].X -= origin.X;
            adjustedModes[index].Y -= origin.Y;
        }
        return (remaining, adjustedModes);
    }

    internal static (DisplayPath[] Paths, Mode[] Modes) ReadActive()
        => ReadTopology(ActivePaths);

    /// <summary>
    /// Reads the same per-monitor scale exposed by Windows Settings. The DPI device-info
    /// packets are source-scoped, so Sunshine's Windows display name is resolved back to
    /// the active DisplayConfig source before querying the value.
    /// </summary>
    public static DisplayScale? TryGetDisplayScale(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) ||
            string.Equals(displayName, "Unattached display", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var path = ReadActive().Paths.FirstOrDefault(candidate =>
                DisplayNamesEqual(GetSourceName(candidate), displayName));
            return path.Equals(default(DisplayPath)) ? null : ReadDisplayScale(path.Source);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Applies a standard Windows scale to an active display.</summary>
    public static bool SetDisplayScale(string? displayName, int scalePercent)
    {
        if (!DpiScalePercents.Contains(scalePercent))
            throw new ArgumentOutOfRangeException(nameof(scalePercent), "Unsupported Windows display scale.");
        if (string.IsNullOrWhiteSpace(displayName) ||
            string.Equals(displayName, "Unattached display", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = ReadActive().Paths.FirstOrDefault(candidate =>
            DisplayNamesEqual(GetSourceName(candidate), displayName));
        if (path.Equals(default(DisplayPath))) return false;
        var current = ReadDisplayScale(path.Source);
        var selectedIndex = Array.IndexOf(DpiScalePercents, scalePercent);
        var recommendedIndex = Array.IndexOf(DpiScalePercents, current.RecommendedPercent);
        if (selectedIndex < 0 || recommendedIndex < 0 || !current.SupportedPercents.Contains(scalePercent))
            throw new ArgumentOutOfRangeException(nameof(scalePercent), "This display does not support the requested Windows scale.");
        if (current.CurrentPercent == scalePercent) return true;

        var request = new DpiScaleSet
        {
            Header = new DpiDeviceHeader
            {
                Type = -4,
                Size = (uint)Marshal.SizeOf<DpiScaleSet>(),
                Adapter = path.Source.Adapter,
                Id = path.Source.Id
            },
            ScaleRelative = selectedIndex - recommendedIndex
        };
        Check(DisplayConfigSetDeviceInfo(ref request));
        for (var attempt = 0; attempt < 10; attempt++) {
            if (ReadDisplayScale(path.Source).CurrentPercent == scalePercent) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>
    /// Applies a client-selected mode to an active display without saving it to the user's
    /// display database. This is used immediately after attaching an idle VDD, whose Windows
    /// best-mode fallback is otherwise commonly 800x600 at 30 Hz.
    /// </summary>
    public static string? ResolveDisplaySource(string? displayName, string? friendlyName = null)
    {
        if (!string.IsNullOrWhiteSpace(displayName) && displayName != "Unattached display") {
            var active = ReadActive().Paths.Where(path => DisplayNamesEqual(GetSourceName(path), displayName)).ToArray();
            if (active.Length == 1) return GetSourceName(active[0]);
        }
        var paths = ReadTopology(AllPaths).Paths.Where(path => path.Target.Available != 0 &&
            MatchesRequestedDisplay(GetSourceName(path), GetTargetName(path).FriendlyName, displayName, friendlyName))
            .GroupBy(path => (path.Target.Adapter.Low, path.Target.Adapter.High, path.Target.Id)).ToArray();
        if (paths.Length != 1) return null;
        return paths[0].Select(GetSourceName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    public static IReadOnlyList<DisplayMode> GetDisplayModes(string? displayName)
    {
        var result = new List<DisplayMode>();
        if (string.IsNullOrWhiteSpace(displayName) || displayName == "Unattached display") return result;
        for (var index = 0; ; index++) {
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(displayName, index, ref mode)) break;
            if (mode.BitsPerPel >= 32 && mode.PelsWidth >= 640 && mode.PelsHeight >= 360 && mode.DisplayFrequency >= 30)
                result.Add(new((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency));
        }
        return result.Distinct().OrderBy(mode => mode.Width).ThenBy(mode => mode.Height).ThenBy(mode => mode.Fps).ToArray();
    }

    public static bool ValidateDisplayMode(string? displayName, int width, int height, int fps)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        for (var index = 0; ; index++) {
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(displayName, index, ref mode)) return false;
            if (mode.PelsWidth == width && mode.PelsHeight == height && mode.DisplayFrequency == fps && mode.BitsPerPel >= 32) {
                // CDS_TEST rejects detached source routes on some IddCx drivers. Enumeration
                // validates their advertised capability; the transaction tests again after attachment.
                var active = ReadActive().Paths.Any(path => DisplayNamesEqual(GetSourceName(path), displayName));
                return !active || ChangeDisplaySettingsEx(displayName, ref mode, IntPtr.Zero, 2, IntPtr.Zero) == 0;
            }
        }
    }

    public static bool SetTemporaryDisplayMode(string? displayName, int width, int height, int fps = 60)
    {
        if (string.IsNullOrWhiteSpace(displayName) || width <= 0 || height <= 0) return false;
        var candidates = new List<DevMode>();
        for (var index = 0; ; index++)
        {
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(displayName, index, ref mode)) break;
            if (mode.PelsWidth == width && mode.PelsHeight == height && mode.BitsPerPel >= 32 && mode.DisplayFrequency == fps)
                candidates.Add(mode);
        }
        if (candidates.Count == 0) return false;

        // Only the exact client-selected mode is eligible.
        var selected = candidates
            .OrderBy(mode => Math.Abs((int)mode.DisplayFrequency - fps))
            .ThenByDescending(mode => mode.DisplayFrequency)
            .First();
        if (ChangeDisplaySettingsEx(displayName, ref selected, IntPtr.Zero, 2, IntPtr.Zero) != 0)
            return false; // CDS_TEST
        if (ChangeDisplaySettingsEx(displayName, ref selected, IntPtr.Zero, 0, IntPtr.Zero) != 0) return false;
        var actual = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(displayName, -1, ref actual) && actual.PelsWidth == width &&
            actual.PelsHeight == height && actual.DisplayFrequency == fps;
    }

    internal static DisplayScale DecodeDisplayScale(int minimumRelative, int currentRelative, int maximumRelative)
    {
        // Windows expresses every value as an offset from its recommended scale. Its
        // minimum is 100%, which lets us recover the absolute recommended list index.
        var recommendedIndex = -minimumRelative;
        var currentIndex = recommendedIndex + currentRelative;
        var maximumIndex = recommendedIndex + maximumRelative;
        if (recommendedIndex < 0 || recommendedIndex >= DpiScalePercents.Length ||
            currentIndex < 0 || currentIndex >= DpiScalePercents.Length ||
            maximumIndex < 0 || maximumIndex >= DpiScalePercents.Length ||
            maximumIndex < recommendedIndex + minimumRelative || currentIndex > maximumIndex)
            throw new InvalidOperationException("Windows returned an unsupported display-scale range.");
        return new DisplayScale(
            DpiScalePercents[currentIndex],
            DpiScalePercents[recommendedIndex],
            DpiScalePercents.Take(maximumIndex + 1).ToArray());
    }

    private static DisplayScale ReadDisplayScale(Source source)
    {
        var request = new DpiScaleGet
        {
            Header = new DpiDeviceHeader
            {
                Type = -3,
                Size = (uint)Marshal.SizeOf<DpiScaleGet>(),
                Adapter = source.Adapter,
                Id = source.Id
            }
        };
        Check(DisplayConfigGetDeviceInfo(ref request));
        return DecodeDisplayScale(request.MinimumRelative, request.CurrentRelative, request.MaximumRelative);
    }

    private static bool DisplayNamesEqual(string first, string second)
    {
        static string Normalize(string value) => value.Trim().Replace(@"\\.\", "", StringComparison.OrdinalIgnoreCase);
        return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
    }

    private static (DisplayPath[] Paths, Mode[] Modes) ReadTopology(uint queryFlags)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(queryFlags, out var pathCount, out var modeCount));
            var paths = new DisplayPath[pathCount];
            var modes = new Mode[modeCount];
            var error = QueryDisplayConfig(queryFlags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == 122) continue; // Topology changed between buffer sizing and reading.
            Check(error);
            return (paths.Take((int)pathCount).ToArray(), modes.Take((int)modeCount).ToArray());
        }
        throw new InvalidOperationException("The display layout is still changing. Try saving again.");
    }

    private static string GetSourceName(DisplayPath path)
    {
        var name = new SourceName
        {
            Header = new DeviceHeader { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(),
                Adapter = path.Source.Adapter, Id = path.Source.Id }
        };
        Check(DisplayConfigGetDeviceInfo(ref name));
        return name.Name;
    }

    private static bool IsVirtual(DisplayPath path)
    {
        var target = GetTargetName(path);
        var adapter = GetDisplayAdapter(GetSourceName(path));
        // Indirect wired output alone is insufficient: physical DisplayLink monitors use it too.
        return IsVirtualIdentity(target.FriendlyName, adapter.DeviceString, adapter.DeviceId);
    }

    internal static bool IsVirtualIdentity(string? targetFriendlyName, string? adapterName, string? adapterDeviceId) =>
        IsVirtualName(targetFriendlyName) ||
        IsVirtualName(adapterName) ||
        IsVirtualName(adapterDeviceId) ||
        adapterName?.Contains("GameViewer", StringComparison.OrdinalIgnoreCase) == true ||
        adapterDeviceId?.Contains("GameViewer", StringComparison.OrdinalIgnoreCase) == true ||
        adapterDeviceId?.Contains("MttVDD", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool MatchesRequestedDisplay(
        string sourceName,
        string targetFriendlyName,
        string? expectedSourceName,
        string? expectedFriendlyName) =>
        (!string.IsNullOrWhiteSpace(expectedSourceName) &&
         !string.Equals(expectedSourceName, "Unattached display", StringComparison.OrdinalIgnoreCase) &&
         DisplayNamesEqual(sourceName, expectedSourceName)) ||
        ((string.IsNullOrWhiteSpace(expectedSourceName) || expectedSourceName == "Unattached display") &&
        (!string.IsNullOrWhiteSpace(expectedFriendlyName) &&
         string.Equals(targetFriendlyName.Trim(), expectedFriendlyName.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static bool IsVirtualName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("IDD", StringComparison.OrdinalIgnoreCase));

    private static DisplayDevice GetDisplayAdapter(string displayName)
    {
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(IntPtr.Zero, index, ref device, 0)) break;
            if (DisplayNamesEqual(device.DeviceName, displayName)) return device;
        }
        return new DisplayDevice();
    }

    private static TargetName GetTargetName(DisplayPath path)
    {
        var name = new TargetName
        {
            Header = new DeviceHeader { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(),
                Adapter = path.Target.Adapter, Id = path.Target.Id }
        };
        Check(DisplayConfigGetDeviceInfo(ref name));
        return name;
    }

    private static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Source { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Target
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator,
            ScanLineOrdering;
        public int Available;
        public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayPath { public Source Source; public Target Target; public uint Flags; }
    // Preserve the entire native union, including target signal timings, without interpretation.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct Mode
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid Adapter;
        [FieldOffset(16)] public ulong Data0;
        [FieldOffset(24)] public ulong Data1;
        [FieldOffset(32)] public ulong Data2;
        [FieldOffset(40)] public ulong Data3;
        [FieldOffset(48)] public ulong Data4;
        [FieldOffset(56)] public ulong Data5;
        [FieldOffset(28)] public int X;
        [FieldOffset(32)] public int Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceHeader { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DpiDeviceHeader { public int Type; public uint Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleGet
    {
        public DpiDeviceHeader Header;
        public int MinimumRelative, CurrentRelative, MaximumRelative;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleSet { public DpiDeviceHeader Header; public int ScaleRelative; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public DeviceHeader Header;
        public uint Flags, Technology;
        public ushort Manufacturer, Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency,
            IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2,
            PanningWidth, PanningHeight;
    }
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, [Out] Mode[] modes, IntPtr topology);
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] DisplayPath[] paths,
        uint modeCount, [In] Mode[] modes, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetName name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        IntPtr device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode mode);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string deviceName,
        ref DevMode mode,
        IntPtr window,
        uint flags,
        IntPtr parameters);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DpiScaleGet scale);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int DisplayConfigSetDeviceInfo(ref DpiScaleSet scale);
}
