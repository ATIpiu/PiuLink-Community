using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace PiuLink.Host.Services;

internal sealed record WindowMoveResult(int Moved, int AlreadyOnTarget, int Failed);

internal static class WindowPlacementService
{
    private const uint GwOwner = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpAsyncWindowPos = 0x4000;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;

    private sealed record SavedWindow(IntPtr Handle, uint ProcessId, uint ThreadId,
        string Display, System.Drawing.Rectangle WorkArea, WindowPlacement Placement,
        NativeRect ScreenPosition, uint Dpi);
    private static List<SavedWindow> _desktopWindows = [];
    private sealed record PendingRestore(SavedWindow Window, WindowPlacement Placement, int Attempts, long RetryAfter);
    private static readonly Dictionary<IntPtr, PendingRestore> PendingRestores = new();
    private sealed record LocalApplicationLayout(SavedWindow Window, bool Original, bool Pending = false, bool Minimize = false, int Attempts = 0, long RetryAfter = 0);
    private static readonly Dictionary<IntPtr, LocalApplicationLayout> LocalApplications = new();
    private static string? _localRestoreLayout;
    private static long _localRestoreSince;

    public static void PreserveLocalApplicationLayout(IntPtr handle)
    {
        var currentThread = GetWindowThreadProcessId(handle, out var currentPid);
        if (currentThread == 0) return;
        if (LocalApplications.TryGetValue(handle, out var existing)) {
            if (currentThread == existing.Window.ThreadId && currentPid == existing.Window.ProcessId) {
                // Reconnecting cancels a pending restore, but keeps the original local geometry.
                LocalApplications[handle] = existing with { Pending = false };
                return;
            }
            LocalApplications.Remove(handle);
        }
        var saved = _desktopWindows.FirstOrDefault(window => window.Handle == handle &&
            window.ProcessId == currentPid && window.ThreadId == currentThread);
        bool original = saved is not null;
        if (saved is null) {
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            var thread = GetWindowThreadProcessId(handle, out var pid);
            if (thread == 0 || !GetWindowPlacement(handle, ref placement)) return;
            var screen = Forms.Screen.FromHandle(handle);
            saved = new(handle, pid, thread, screen.DeviceName, screen.WorkingArea, placement,
                PlacementToScreen(handle, placement.NormalPosition, screen), GetDpiForWindow(handle));
            original = DisplayTopologyService.ReadDesktopState().PhysicalOnly;
        }
        LocalApplications[handle] = new(saved, original);
    }

    public static void ScheduleLocalApplicationRestore(IntPtr handle, bool minimize)
    {
        if (LocalApplications.TryGetValue(handle, out var layout)) LocalApplications[handle] = layout with { Pending = true, Minimize = minimize };
    }

    public static void RestoreLocalApplications(bool physicalOnly, string desktopLayout, long now)
    {
        // Closed applications may never receive a disconnect/restore request. Remove their
        // snapshots even during streaming so recycled HWNDs cannot suppress general recovery.
        foreach (var entry in LocalApplications.ToArray()) {
            var thread = GetWindowThreadProcessId(entry.Key, out var pid);
            if (thread != entry.Value.Window.ThreadId || pid != entry.Value.Window.ProcessId)
                LocalApplications.Remove(entry.Key);
        }
        if (!physicalOnly) { _localRestoreLayout = null; return; }
        if (_localRestoreLayout != desktopLayout) { _localRestoreLayout = desktopLayout; _localRestoreSince = now; return; }
        if (now - _localRestoreSince < 3000) return;
        foreach (var entry in LocalApplications.ToArray()) {
            var local = entry.Value;
            if (!local.Pending) continue;
            var saved = local.Window;
            var thread = GetWindowThreadProcessId(saved.Handle, out var pid);
            if (thread != saved.ThreadId || pid != saved.ProcessId) { LocalApplications.Remove(entry.Key); continue; }
            var placement = saved.Placement;
            bool originalMonitorReady = Forms.Screen.AllScreens.Any(s => s.DeviceName == saved.Display && s.WorkingArea == saved.WorkArea);
            if (local.Original && !originalMonitorReady && now - _localRestoreSince < 15000) continue;
            if (!local.Original || !originalMonitorReady) {
                var screen = Forms.Screen.PrimaryScreen;
                if (screen is null) continue;
                var area = screen.WorkingArea;
                var rect = new NativeRect { Left = area.Left + area.Width / 10, Top = area.Top + area.Height / 10,
                    Right = area.Right - area.Width / 10, Bottom = area.Bottom - area.Height / 10 };
                placement.NormalPosition = ScreenToPlacement(saved.Handle, rect, screen);
                placement.ShowCommand = 1;
            }
            if (local.Minimize) placement.ShowCommand = SwShowMinimized;
            placement.Flags |= 4;
            var actualPlacement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (local.Attempts > 0 && GetWindowPlacement(saved.Handle, ref actualPlacement) &&
                PlacementMatches(actualPlacement.NormalPosition, placement.NormalPosition, 3) && actualPlacement.ShowCommand == placement.ShowCommand) {
                LocalApplications.Remove(entry.Key);
                // The general recovery pass must not issue a competing asynchronous placement.
                _desktopWindows.RemoveAll(window => window.Handle == saved.Handle && window.ProcessId == saved.ProcessId);
            } else if (local.Attempts < 5 && Environment.TickCount64 >= local.RetryAfter) {
                SetWindowPlacement(saved.Handle, ref placement);
                LocalApplications[entry.Key] = local with { Attempts = local.Attempts + 1, RetryAfter = Environment.TickCount64 + 1000 };
            }
        }
    }
    private static string? _streamLayout;
    private static long _streamLayoutSince;
    private static bool _streamLayoutApplied;
    private static readonly HashSet<IntPtr> VerifiedStreamWindows = new();
    private static readonly Dictionary<IntPtr, NativeRect> PendingStreamWindows = new();
    private static int _streamLayoutAttempts;
    private static readonly DesktopRecoveryTracker Recovery = new();
    public static bool SessionRestorePending => Recovery.Pending || Recovery.Waiting || PendingRestores.Count > 0;
    public static bool HasSessionTransition => Recovery.Pending;
    public static void BeginSessionDesktopTransition() => Recovery.BeginTransition();

    public static void CaptureSessionDesktop(string layout)
    {
        if (Recovery.CaptureBaseline(layout))
            _desktopWindows = CaptureDesktopWindows();
    }

    /// <summary>
    /// Keep a fresh physical-desktop snapshot while idle, freeze it during streaming,
    /// then restore only after the original layout has settled. A changed physical layout
    /// gets a grace period for delayed monitors before stale coordinates are discarded.
    /// </summary>
    public static WindowMoveResult ObserveSessionDesktop(bool physicalDesktopOnly, string layout = "")
    {
        ResetStreamLayout();
        var now = Environment.TickCount64;
        var verification = VerifyPendingRestores(physicalDesktopOnly, now);
        if (PendingRestores.Count > 0 || verification.Moved > 0 || verification.Failed > 0)
            return verification;
        var action = Recovery.Observe(physicalDesktopOnly, layout, now);
        if (action == DesktopRecoveryAction.Wait)
        {
            return new(0, 0, 0);
        }

        var moved = 0;
        var failed = 0;
        if (action == DesktopRecoveryAction.Restore)
        {
            foreach (var saved in _desktopWindows)
            {
                if (LocalApplications.ContainsKey(saved.Handle)) continue; // Virtual apps restore their own local show state.
                var thread = GetWindowThreadProcessId(saved.Handle, out var process);
                if (thread != saved.ThreadId || process != saved.ProcessId) continue;
                // Avoid restoring stale coordinates if monitors were unplugged or rearranged.
                if (!Forms.Screen.AllScreens.Any(screen => screen.DeviceName == saved.Display &&
                    screen.WorkingArea == saved.WorkArea)) continue;
                var placement = saved.Placement;
                var current = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
                if (!GetWindowPlacement(saved.Handle, ref current)) { failed++; continue; }
                // Preserve minimize/maximize choices made during the remote session.
                placement.ShowCommand = current.ShowCommand;
                placement.Flags = (placement.Flags & ~2) | (current.Flags & 2);
                placement.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: never hang on another app.
                if (SetWindowPlacement(saved.Handle, ref placement))
                {
                    moved++;
                    PendingRestores[saved.Handle] = new(saved, placement, 1, now + 500);
                }
                else failed++;
            }
            _desktopWindows.Clear();
        }
        else if (action == DesktopRecoveryAction.Capture)
        {
            _desktopWindows = CaptureDesktopWindows();
        }
        else _desktopWindows.Clear();
        return new(moved, 0, failed);
    }

    public static void ResetSessionDesktop()
    {
        ResetStreamLayout();
        _desktopWindows.Clear();
        PendingRestores.Clear();
        Recovery.Reset();
    }

    private static WindowMoveResult VerifyPendingRestores(bool physicalOnly, long now)
    {
        if (!physicalOnly)
        {
            PendingRestores.Clear();
            return new(0, 0, 0);
        }
        var retried = 0;
        var failed = 0;
        foreach (var entry in PendingRestores.ToArray())
        {
            var pending = entry.Value;
            var saved = pending.Window;
            var thread = GetWindowThreadProcessId(saved.Handle, out var pid);
            if (thread != saved.ThreadId || pid != saved.ProcessId)
            {
                PendingRestores.Remove(entry.Key);
                continue;
            }
            var current = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (GetWindowPlacement(saved.Handle, ref current) &&
                PlacementMatches(current.NormalPosition, pending.Placement.NormalPosition, 3))
            {
                PendingRestores.Remove(entry.Key);
                continue;
            }
            if (now < pending.RetryAfter) continue;
            if (pending.Attempts >= 5)
            {
                PendingRestores.Remove(entry.Key);
                failed++;
                continue;
            }
            var placement = pending.Placement;
            if (SetWindowPlacement(saved.Handle, ref placement))
            {
                PendingRestores[entry.Key] = pending with { Attempts = pending.Attempts + 1, RetryAfter = now + 500 };
                retried++;
            }
            else
            {
                PendingRestores.Remove(entry.Key);
                failed++;
            }
        }
        return new(retried, 0, failed);
    }

    internal static bool PlacementMatches(NativeRect actual, NativeRect expected, int tolerance) =>
        Math.Abs(actual.Left - expected.Left) <= tolerance &&
        Math.Abs(actual.Top - expected.Top) <= tolerance &&
        Math.Abs(actual.Right - expected.Right) <= tolerance &&
        Math.Abs(actual.Bottom - expected.Bottom) <= tolerance;

    public static void ResetStreamLayout()
    {
        _streamLayout = null;
        _streamLayoutApplied = false;
    }

    // Apply once per stable virtual layout, so normal resizing during streaming is respected.
    // A virtual streaming desktop can coexist with physical monitors, so the selected virtual
    // display must be identified explicitly instead of assuming Windows exposes one screen.
    public static WindowMoveResult AdaptToVirtualDesktop(string layout, string targetDisplayName, long now)
    {
        if (_streamLayout != layout)
        {
            _streamLayout = layout;
            _streamLayoutSince = now;
            VerifiedStreamWindows.Clear(); PendingStreamWindows.Clear(); _streamLayoutAttempts = 0;
            _streamLayoutApplied = false;
        }
        if (_streamLayoutApplied || now - _streamLayoutSince < 1500) return new(0, 0, 0);
        if (_desktopWindows.Count == 0) return new(0, 0, 0);
        var target = Forms.Screen.AllScreens.FirstOrDefault(screen => string.Equals(
            screen.DeviceName, targetDisplayName, StringComparison.OrdinalIgnoreCase));
        // Sunshine may publish the route before Windows has attached the virtual desktop.
        // Keep retrying on the recovery timer until that display is actually available.
        if (target is null) return new(0, 0, 0);
        var moved = 0;
        var failed = 0;
        _streamLayoutAttempts++;
        foreach (var saved in _desktopWindows)
        {
            if (VerifiedStreamWindows.Contains(saved.Handle)) continue;
            var thread = GetWindowThreadProcessId(saved.Handle, out var process);
            if (thread != saved.ThreadId || process != saved.ProcessId) continue;
            var current = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(saved.Handle, ref current)) continue;
            // Do not unmaximize, reveal minimized windows, or change keyboard focus.
            if (current.ShowCommand != 1) continue;
            var rect = ScaleAndClamp(saved.ScreenPosition, saved.WorkArea, target.WorkingArea,
                saved.Dpi, GetDpiForWindow(saved.Handle));
            if (PendingStreamWindows.TryGetValue(saved.Handle, out var expected) && GetWindowRect(saved.Handle, out var observed)
                && PlacementMatches(observed, expected, 3)) {
                VerifiedStreamWindows.Add(saved.Handle); PendingStreamWindows.Remove(saved.Handle); continue;
            }
            if (_streamLayoutAttempts > 5) { failed++; continue; }
            PendingStreamWindows[saved.Handle] = rect;
            if (SetWindowPos(saved.Handle, IntPtr.Zero, rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos)) moved++;
            else failed++;
        }
        _streamLayoutApplied = PendingStreamWindows.Count == 0 && failed == 0;
        if (_streamLayoutAttempts > 5) _streamLayoutApplied = true; // Bounded retries; failures remain visible.
        return new(moved, 0, failed);
    }

    private static List<SavedWindow> CaptureDesktopWindows()
    {
        var saved = new List<SavedWindow>();
        var shell = GetShellWindow();
        EnumWindows((window, _) =>
        {
            if (!IsApplicationWindow(window, shell)) return true;
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            var thread = GetWindowThreadProcessId(window, out var process);
            if (thread != 0 && GetWindowPlacement(window, ref placement))
            {
                var screen = Forms.Screen.FromHandle(window);
                saved.Add(new(window, process, thread, screen.DeviceName, screen.WorkingArea, placement,
                    PlacementToScreen(window, placement.NormalPosition, screen), GetDpiForWindow(window)));
            }
            return true;
        }, IntPtr.Zero);
        return saved;
    }

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
        "Windows.UI.Core.CoreWindow"
    };

    public static WindowMoveResult MoveApplicationWindows(string targetDisplayName)
    {
        var target = Forms.Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, targetDisplayName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            throw new InvalidOperationException($"Windows could not find display {targetDisplayName}.");
        }

        var moved = 0;
        var alreadyOnTarget = 0;
        var failed = 0;
        var shellWindow = GetShellWindow();
        EnumWindows((window, _) =>
        {
            if (!IsApplicationWindow(window, shellWindow))
            {
                return true;
            }

            var source = Forms.Screen.FromHandle(window);
            if (string.Equals(source.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                alreadyOnTarget++;
                return true;
            }

            if (MoveWindow(window, source, target))
            {
                moved++;
            }
            else
            {
                failed++;
            }

            return true;
        }, IntPtr.Zero);

        return new WindowMoveResult(moved, alreadyOnTarget, failed);
    }

    public static WindowMoveResult MoveApplicationWindowsFromDisplay(
        string sourceDisplayName,
        string targetDisplayName)
    {
        var screens = Forms.Screen.AllScreens;
        var source = screens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, sourceDisplayName, StringComparison.OrdinalIgnoreCase));
        var target = screens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, targetDisplayName, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            throw new InvalidOperationException($"Windows could not find display {sourceDisplayName}.");
        }

        if (target is null)
        {
            throw new InvalidOperationException($"Windows could not find display {targetDisplayName}.");
        }

        var moved = 0;
        var alreadyOnTarget = 0;
        var failed = 0;
        var shellWindow = GetShellWindow();
        EnumWindows((window, _) =>
        {
            if (!IsApplicationWindow(window, shellWindow))
            {
                return true;
            }

            var current = Forms.Screen.FromHandle(window);
            if (string.Equals(current.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                alreadyOnTarget++;
                return true;
            }

            if (!string.Equals(current.DeviceName, source.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (MoveWindow(window, source, target))
            {
                moved++;
            }
            else
            {
                failed++;
            }

            return true;
        }, IntPtr.Zero);

        return new WindowMoveResult(moved, alreadyOnTarget, failed);
    }

    public static WindowMoveResult RecoverOffscreenApplicationWindows(string? targetDisplayName = null)
    {
        var screens = Forms.Screen.AllScreens;
        var target = string.IsNullOrWhiteSpace(targetDisplayName)
            ? Forms.Screen.PrimaryScreen
            : screens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, targetDisplayName, StringComparison.OrdinalIgnoreCase));
        target ??= screens.FirstOrDefault();
        if (target is null)
        {
            throw new InvalidOperationException("Windows did not report an active display.");
        }

        var moved = 0;
        var alreadyReachable = 0;
        var failed = 0;
        var shellWindow = GetShellWindow();
        EnumWindows((window, _) =>
        {
            if (!IsApplicationWindow(window, shellWindow))
            {
                return true;
            }

            var placement = new WindowPlacement
            {
                Length = Marshal.SizeOf<WindowPlacement>()
            };
            if (!GetWindowPlacement(window, ref placement))
            {
                failed++;
                return true;
            }

            var source = Forms.Screen.FromHandle(window);
            var screenPosition = PlacementToScreen(window, placement.NormalPosition, source);
            if (IsReachable(screenPosition, screens))
            {
                alreadyReachable++;
                return true;
            }

            if (MoveWindow(window, source, target))
            {
                moved++;
            }
            else
            {
                failed++;
            }

            return true;
        }, IntPtr.Zero);

        return new WindowMoveResult(moved, alreadyReachable, failed);
    }

    private static bool IsApplicationWindow(IntPtr window, IntPtr shellWindow)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        if (window == IntPtr.Zero ||
            window == shellWindow ||
            processId == (uint)Environment.ProcessId ||
            !IsWindowVisible(window) ||
            GetWindow(window, GwOwner) != IntPtr.Zero ||
            GetWindowTextLength(window) == 0)
        {
            return false;
        }

        var className = new char[256];
        var classNameLength = GetClassName(window, className, className.Length);
        return classNameLength <= 0 ||
               !ShellWindowClasses.Contains(new string(className, 0, classNameLength));
    }

    private static bool MoveWindow(IntPtr window, Forms.Screen source, Forms.Screen target)
    {
        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>()
        };
        if (!GetWindowPlacement(window, ref placement))
        {
            return false;
        }

        var sourceArea = source.WorkingArea;
        var targetArea = target.WorkingArea;
        var normal = PlacementToScreen(window, placement.NormalPosition, source);
        var translated = TranslateAndClamp(normal, sourceArea, targetArea);
        placement.Flags |= 4; // Do not block on another process's UI thread.

        if (placement.ShowCommand == SwShowMinimized)
        {
            placement.NormalPosition = ScreenToPlacement(window, translated, target);
            return SetWindowPlacement(window, ref placement);
        }

        if (placement.ShowCommand == SwShowMaximized)
        {
            placement.NormalPosition = ScreenToPlacement(window, translated, target);
            if (!SetWindowPlacement(window, ref placement))
            {
                return false;
            }

            return SetWindowPos(
                window,
                IntPtr.Zero,
                targetArea.Left,
                targetArea.Top,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
        }

        return SetWindowPos(
            window,
            IntPtr.Zero,
            translated.Left,
            translated.Top,
            translated.Right - translated.Left,
            translated.Bottom - translated.Top,
            SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
    }

    private static NativeRect PlacementToScreen(IntPtr window, NativeRect rect, Forms.Screen screen) =>
        OffsetPlacement(rect, screen.Bounds, screen.WorkingArea, UsesWorkspaceCoordinates(window), false);

    private static NativeRect ScreenToPlacement(IntPtr window, NativeRect rect, Forms.Screen screen) =>
        OffsetPlacement(rect, screen.Bounds, screen.WorkingArea, UsesWorkspaceCoordinates(window), true);

    private static bool UsesWorkspaceCoordinates(IntPtr window) =>
        (GetWindowLong(window, -20) & 0x80) == 0; // WS_EX_TOOLWINDOW uses screen coordinates.

    internal static NativeRect OffsetPlacement(NativeRect rect, System.Drawing.Rectangle bounds,
        System.Drawing.Rectangle workArea, bool workspace, bool toWorkspace)
    {
        var sign = toWorkspace ? -1 : 1;
        var dx = workspace ? sign * (workArea.Left - bounds.Left) : 0;
        var dy = workspace ? sign * (workArea.Top - bounds.Top) : 0;
        return new(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
    }

    internal static NativeRect TranslateAndClamp(
        NativeRect window,
        System.Drawing.Rectangle sourceArea,
        System.Drawing.Rectangle targetArea)
    {
        var width = Math.Max(1, window.Right - window.Left);
        var height = Math.Max(1, window.Bottom - window.Top);
        width = Math.Min(width, targetArea.Width);
        height = Math.Min(height, targetArea.Height);

        var translatedLeft = targetArea.Left + (window.Left - sourceArea.Left);
        var translatedTop = targetArea.Top + (window.Top - sourceArea.Top);
        translatedLeft = Math.Clamp(translatedLeft, targetArea.Left, targetArea.Right - width);
        translatedTop = Math.Clamp(translatedTop, targetArea.Top, targetArea.Bottom - height);
        return new NativeRect(
            translatedLeft,
            translatedTop,
            translatedLeft + width,
            translatedTop + height);
    }

    internal static NativeRect ScaleAndClamp(NativeRect window, System.Drawing.Rectangle sourceArea,
        System.Drawing.Rectangle targetArea, uint sourceDpi, uint targetDpi)
    {
        var scale = sourceDpi == 0 || targetDpi == 0 ? 1.0 : (double)targetDpi / sourceDpi;
        var width = Math.Clamp((int)Math.Round((window.Right - window.Left) * scale), 1, targetArea.Width);
        var height = Math.Clamp((int)Math.Round((window.Bottom - window.Top) * scale), 1, targetArea.Height);
        var left = targetArea.Left + (int)Math.Round((window.Left - sourceArea.Left) * scale);
        var top = targetArea.Top + (int)Math.Round((window.Top - sourceArea.Top) * scale);
        left = Math.Clamp(left, targetArea.Left, targetArea.Right - width);
        top = Math.Clamp(top, targetArea.Top, targetArea.Bottom - height);
        return new(left, top, left + width, top + height);
    }

    private static bool IsReachable(NativeRect window, IReadOnlyList<Forms.Screen> screens)
    {
        var rectangle = ToRectangle(window);
        return screens.Any(screen =>
        {
            var intersection = System.Drawing.Rectangle.Intersect(rectangle, screen.WorkingArea);
            return intersection.Width >= Math.Min(64, rectangle.Width) &&
                   intersection.Height >= Math.Min(32, rectangle.Height);
        });
    }

    private static System.Drawing.Rectangle ToRectangle(NativeRect rectangle) =>
        System.Drawing.Rectangle.FromLTRB(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
