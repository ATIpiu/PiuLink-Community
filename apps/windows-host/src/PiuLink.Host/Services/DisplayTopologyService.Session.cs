using System.Security.Cryptography;
using System.Text;

namespace PiuLink.Host.Services;

internal static partial class DisplayTopologyService
{
    internal sealed record SessionOutput(string Key, string Name, bool Active, bool Virtual, bool Capture);
    internal sealed record VirtualDesktopMode(string Key, DisplayMode Current, IReadOnlyList<DisplayMode> Modes);

    // A cloned source also drives another target. Changing its mode would resize both desktops.
    private static string? VirtualCaptureSource(string? key)
    {
        if (key == null) return null;
        var paths = ReadActive().Paths;
        var targets = paths.Where(p => OutputKey(GetTargetName(p).DevicePath) == key).ToArray();
        if (targets.Length != 1 || !IsVirtual(targets[0])) return null;
        var source = targets[0].Source;
        return paths.Count(p => p.Source.Adapter.Equals(source.Adapter) && p.Source.Id == source.Id) == 1
            ? GetSourceName(targets[0]) : null;
    }

    private static DisplayMode? CurrentDesktopMode(string source)
    {
        var mode = new DevMode { Size = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(source, -1, ref mode)
            ? new((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency) : null;
    }

    internal static VirtualDesktopMode? GetVirtualDesktopMode(string? captureKey)
    {
        var source = VirtualCaptureSource(captureKey);
        if (source == null || CurrentDesktopMode(source) is not { } current) return null;
        return new(captureKey!, current, GetDisplayModes(source).Where(m => m.Fps == current.Fps).ToArray());
    }

    internal static void ResizeVirtualDesktop(string captureKey, string expectedKey, int width, int height)
    {
        if (captureKey != expectedKey) throw new InvalidOperationException("outputs_changed");
        var source = VirtualCaptureSource(captureKey) ?? throw new InvalidOperationException("output_protected");
        if (CurrentDesktopMode(source) is not { } before) throw new InvalidOperationException("outputs_changed");
        var requested = new DisplayMode(width, height, before.Fps);
        if (requested == before) return;
        if (!GetDisplayModes(source).Contains(requested)) throw new InvalidOperationException("display_mode_unavailable");
        bool Change(DisplayMode target, bool test) {
            if (VirtualCaptureSource(captureKey) != source) throw new InvalidOperationException("outputs_changed");
            var mode = new DevMode {
                Size = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DevMode>(),
                Fields = 0x80000 | 0x100000 | 0x400000, // DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY
                PelsWidth = (uint)target.Width, PelsHeight = (uint)target.Height, DisplayFrequency = (uint)target.Fps
            };
            return ChangeDisplaySettingsEx(source, ref mode, IntPtr.Zero, test ? 2u : 0u, IntPtr.Zero) == 0;
        }
        bool Confirm(DisplayMode target) {
            for (int attempt = 0; attempt < 10; attempt++) {
                if (attempt > 0) Thread.Sleep(100);
                if (VirtualCaptureSource(captureKey) == source && CurrentDesktopMode(source) == target) return true;
            }
            return false;
        }
        ApplyLiveMode(before, requested, Change, Confirm);
    }

    internal static void ApplyLiveMode(DisplayMode before, DisplayMode requested,
        Func<DisplayMode, bool, bool> change, Func<DisplayMode, bool> confirm)
    {
        if (!change(requested, true) || !change(requested, false))
            throw new InvalidOperationException("display_mode_unavailable");
        if (confirm(requested)) return;
        if (!confirm(before) && (!change(before, false) || !confirm(before)))
            throw new InvalidOperationException("output_restore_pending");
        throw new InvalidOperationException("display_mode_unavailable");
    }

    private static string OutputKey(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    internal static string? CaptureOutputKey(string? source, string? name)
    {
        var active = ReadActive().Paths;
        var matches = string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(name)
            ? active
            : active.Where(p => MatchesRequestedDisplay(GetSourceName(p), GetTargetName(p).FriendlyName, source, name)).ToArray();
        if (matches.Length != 1) return null;
        var path = GetTargetName(matches[0]).DevicePath;
        return string.IsNullOrWhiteSpace(path) ? null : OutputKey(path);
    }

    internal static System.Drawing.Rectangle? CaptureBounds(string? captureKey)
    {
        if (captureKey == null) return null;
        foreach (var path in ReadActive().Paths) {
            if (OutputKey(GetTargetName(path).DevicePath) != captureKey) continue;
            var source = GetSourceName(path);
            return System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, source, StringComparison.OrdinalIgnoreCase))?.Bounds;
        }
        return null;
    }

    internal static SessionOutput[] SessionOutputs(string? captureKey)
    {
        var (paths, _) = ReadTopology(AllPaths);
        return paths.Where(p => p.Target.Available != 0).Select(p => (Path: p, Name: GetTargetName(p)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name.DevicePath))
            .GroupBy(p => p.Name.DevicePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => {
                var item = group.OrderByDescending(p => (p.Path.Flags & PathActive) != 0).First();
                var key = OutputKey(group.Key);
                return new SessionOutput(key, string.IsNullOrWhiteSpace(item.Name.FriendlyName)
                    ? GetSourceName(item.Path) : item.Name.FriendlyName, (item.Path.Flags & PathActive) != 0,
                    IsVirtual(item.Path), key == captureKey);
            }).OrderByDescending(p => p.Capture).ThenByDescending(p => p.Active).ThenBy(p => p.Name).ToArray();
    }

    internal static bool CanSetOutput(IReadOnlyList<SessionOutput> outputs, string key, bool enable)
    {
        var target = outputs.FirstOrDefault(output => output.Key == key);
        return target != null && outputs.Any(output => output.Active && output.Capture) &&
            (enable || (!target.Capture && outputs.Any(output => output.Active && output.Key != key)));
    }

    internal static void SetSessionOutput(string captureKey, string key, bool enable, bool validateOnly = false, Snapshot? original = null)
    {
        var outputs = SessionOutputs(captureKey);
        if (!CanSetOutput(outputs, key, enable)) throw new InvalidOperationException("output_protected");
        if (outputs.Single(output => output.Key == key).Active == enable) return;
        var before = validateOnly ? null : CaptureDesktop();
        var (all, modes) = ReadTopology(AllPaths);
        var active = all.Where(p => (p.Flags & PathActive) != 0).ToArray();
        string Key(DisplayPath p) => OutputKey(GetTargetName(p).DevicePath);
        var activeKeys = active.Select(Key).ToHashSet(StringComparer.Ordinal);
        if (!activeKeys.Contains(captureKey) || !activeKeys.SetEquals(outputs.Where(o => o.Active).Select(o => o.Key)))
            throw new InvalidOperationException("outputs_changed");

        Mode? restoredMode = null;
        if (enable && original != null) {
            Mode? OriginalMode(string wanted) {
                int i = Array.FindIndex(original.TargetNames, name => OutputKey(name) == wanted);
                if (i < 0) return null;
                uint index = original.Paths[i].Source.ModeIndex;
                return index < original.Modes.Length && original.Modes[index].Type == 1 ? original.Modes[index] : null;
            }
            if (OriginalMode(key) is Mode saved) {
                foreach (var anchor in active.OrderByDescending(p => Key(p) == captureKey)) {
                    if (anchor.Source.ModeIndex >= modes.Length || OriginalMode(Key(anchor)) is not Mode oldAnchor) continue;
                    var current = modes[anchor.Source.ModeIndex];
                    saved.X += current.X - oldAnchor.X;
                    saved.Y += current.Y - oldAnchor.Y;
                    restoredMode = saved;
                    break;
                }
            }
        }
        DisplayPath[]? proposal = null;
        if (enable)
        {
            foreach (var path in all.Where(p => p.Target.Available != 0 && Key(p) == key &&
                         !active.Any(a => a.Source.Adapter.Equals(p.Source.Adapter) && a.Source.Id == p.Source.Id)))
            {
                var added = path;
                added.Source.ModeIndex = InvalidModeIndex;
                added.Target.ModeIndex = InvalidModeIndex;
                added.Flags |= PathActive;
                if (restoredMode is Mode positioned) {
                    positioned.Adapter = added.Source.Adapter;
                    positioned.Id = added.Source.Id;
                    var withRestored = added;
                    withRestored.Source.ModeIndex = (uint)modes.Length;
                    Mode[] candidateModes = [.. modes, positioned];
                    DisplayPath[] extendedWithRestored = [.. active, withRestored];
                    if (SetDisplayConfig((uint)extendedWithRestored.Length, extendedWithRestored,
                            (uint)candidateModes.Length, candidateModes, Supplied | Validate | AllowChanges) == 0)
                    {
                        modes = candidateModes;
                        proposal = extendedWithRestored;
                        break;
                    }
                }
                DisplayPath[] extended = [.. active, added];
                if (SetDisplayConfig((uint)extended.Length, extended, (uint)modes.Length, modes,
                        Supplied | Validate | AllowChanges) == 0)
                {
                    proposal = extended;
                    break;
                }
            }
            activeKeys.Add(key);
        }
        else
        {
            proposal = active.Where(p => Key(p) != key).ToArray();
            // Windows requires a source at (0,0) when the disabled output was primary.
            if (proposal.Length == 0 || proposal.Any(p => p.Source.ModeIndex >= modes.Length ||
                    modes[p.Source.ModeIndex].Type != 1)) throw new InvalidOperationException("outputs_changed");
            if (!proposal.Any(p => modes[p.Source.ModeIndex].X == 0 && modes[p.Source.ModeIndex].Y == 0))
            {
                var origin = modes[proposal.First(p => Key(p) == captureKey).Source.ModeIndex];
                foreach (var index in proposal.Select(p => p.Source.ModeIndex).Distinct())
                {
                    modes[index].X -= origin.X;
                    modes[index].Y -= origin.Y;
                }
            }
            activeKeys.Remove(key);
        }
        if (proposal is null) throw new InvalidOperationException("output_unavailable");
        Check(SetDisplayConfig((uint)proposal.Length, proposal, (uint)modes.Length, modes, Supplied | Validate | AllowChanges));
        if (validateOnly) return;
        Check(SetDisplayConfig((uint)proposal.Length, proposal, (uint)modes.Length, modes, Supplied | Apply | AllowChanges));
        try
        {
            var actual = ReadActive();
            if (!activeKeys.SetEquals(actual.Paths.Select(Key)) ||
                actual.Paths.Any(p => p.Source.ModeIndex >= actual.Modes.Length || actual.Modes[p.Source.ModeIndex].Type != 1))
                throw new InvalidOperationException("outputs_changed");
        }
        catch
        {
            if (!RestorePhysicalDesktop(before!)) throw new InvalidOperationException("output_restore_pending");
            throw;
        }
    }
}
