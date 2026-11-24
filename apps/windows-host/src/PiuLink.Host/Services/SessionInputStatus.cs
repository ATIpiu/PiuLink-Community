using System.Runtime.InteropServices;
using System.Windows.Automation;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PiuLink.Host.Services;

internal static class SessionInputStatus
{
    // UI Automation can block in a foreign provider. Keep at most one outstanding probe.
    private static int _readingFocus;

    internal sealed record Position(double X, double Y);
    internal sealed record ViewportPoints(Position? Pointer, Position? Caret);

    internal static ViewportPoints Viewport(System.Drawing.Rectangle? bounds, bool includeCaret)
    {
        if (bounds is not { Width: > 0, Height: > 0 } screen) return new(null, null);
        Position? Normalize(System.Drawing.Point p) => screen.Contains(p)
            ? new((p.X - screen.Left) / (double)screen.Width, (p.Y - screen.Top) / (double)screen.Height) : null;
        var pointer = GetPhysicalCursorPos(out var cursor) ? Normalize(cursor) : null;
        var caret = includeCaret ? ReadCaret() : null;
        return new(pointer, caret.HasValue ? Normalize(caret.Value) : null);
    }

    private static System.Drawing.Point? ReadCaret()
    {
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(0, ref gui) && gui.Caret != 0 && gui.Bottom > gui.Top) {
            var point = new System.Drawing.Point(gui.Left, gui.Top + (gui.Bottom - gui.Top) / 2);
            var previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(gui.Caret));
            try {
                if (ClientToScreen(gui.Caret, ref point) && LogicalToPhysicalPointForPerMonitorDPI(gui.Caret, ref point))
                    return point;
            } finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        }
        // Browsers and WPF editors can draw their own caret. Inspect only geometry.
        if (Interlocked.Exchange(ref _readingFocus, 1) != 0) return null;
        try {
            var focused = AutomationElement.FocusedElement;
            if (focused == null || !focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var ranges = ((TextPattern)pattern).GetSelection();
            if (ranges.Length != 1) return null;
            var range = ranges[0];
            var end = System.Windows.Automation.Text.TextPatternRangeEndpoint.End;
            range.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, range, end);
            var rectangles = range.GetBoundingRectangles();
            if (rectangles.Length == 0) {
                range.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Character);
                rectangles = range.GetBoundingRectangles();
            }
            if (rectangles.Length == 0 || rectangles[0].IsEmpty) return null;
            var rect = rectangles[0];
            return new((int)rect.Left, (int)(rect.Top + rect.Height / 2));
        } catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) {
            return null;
        } finally { Volatile.Write(ref _readingFocus, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern bool LogicalToPhysicalPointForPerMonitorDPI(nint window, ref System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);

    public static bool TextInputAtPointer()
    {
        if (Interlocked.Exchange(ref _readingFocus, 1) != 0) return false;
        try {
            var focused = AutomationElement.FocusedElement;
            if (focused == null || !focused.Current.IsEnabled) return false;
            var type = focused.Current.ControlType;
            if (type != ControlType.Edit && type != ControlType.Document && type != ControlType.ComboBox) return false;
            var point = System.Windows.Forms.Cursor.Position;
            var location = new System.Windows.Point(point.X, point.Y);
            if (!focused.Current.BoundingRectangle.Contains(location)) return false;
            var editable = focused.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty, true);
            if (editable is bool readOnly) {
                if (readOnly) return false;
            } else if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) {
                var selection = ((TextPattern)pattern).GetSelection();
                if (selection.Length != 1 ||
                    selection[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false) return false;
            } else if (type != ControlType.Edit || !focused.Current.IsPassword) return false;
            // An overlapping window or a clickable child can cover an editor's bounding box.
            var hit = AutomationElement.FromPoint(location);
            for (int depth = 0; hit != null && depth < 8; depth++) {
                if (Automation.Compare(hit, focused)) return true;
                if (hit.Current.ControlType != ControlType.Text && hit.Current.ControlType != ControlType.Pane)
                    return false;
                hit = TreeWalker.ControlViewWalker.GetParent(hit);
            }
            return false;
        } finally { Volatile.Write(ref _readingFocus, 0); }
    }

    public static bool MicrophoneInUse(string endpointId)
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = devices.GetDevice(endpointId);
        var sessions = endpoint.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++) {
            using var session = sessions[i];
            if (session.State == AudioSessionState.AudioSessionStateActive) return true;
        }
        return false;
    }
}
