namespace PiuLink.Host.Services;

internal enum DesktopRecoveryAction { Capture, Wait, Restore, Discard }

/// <summary>Uses monotonic time so rapid topology notifications cannot finish recovery early.</summary>
internal sealed class DesktopRecoveryTracker
{
    private string? _baseline, _settlingLayout, _idleLayout;
    private long _settlingSince;
    private long _idleSince;
    public bool Pending { get; private set; }
    public bool Waiting { get; private set; }

    public void BeginTransition()
    {
        Pending = true;
        Waiting = true;
        _settlingLayout = null;
        _idleLayout = null;
    }

    public bool CaptureBaseline(string layout)
    {
        // Reconnecting before restoration must not replace the original desktop with
        // windows that Windows has already shrunk to fit the streaming display.
        if (Pending) return false;
        _baseline = layout;
        _idleLayout = layout;
        _settlingLayout = null;
        Pending = false;
        Waiting = false;
        return true;
    }

    public DesktopRecoveryAction Observe(bool physicalOnly, string layout, long nowMilliseconds)
    {
        Waiting = true;
        if (!physicalOnly)
        {
            BeginTransition();
            return DesktopRecoveryAction.Wait;
        }
        if (!Pending)
        {
            if (_idleLayout != layout)
            {
                _idleLayout = layout;
                _idleSince = nowMilliseconds;
                return DesktopRecoveryAction.Wait;
            }
            if (nowMilliseconds - _idleSince < 3000) return DesktopRecoveryAction.Wait;
            _baseline = layout;
            Waiting = false;
            return DesktopRecoveryAction.Capture;
        }
        if (_settlingLayout != layout)
        {
            _settlingLayout = layout;
            _settlingSince = nowMilliseconds;
            return DesktopRecoveryAction.Wait;
        }
        var elapsed = nowMilliseconds - _settlingSince;
        if (elapsed < 3000) return DesktopRecoveryAction.Wait;
        if (_baseline != null && _baseline != layout && elapsed < 15000)
            return DesktopRecoveryAction.Wait;

        var result = _baseline == layout ? DesktopRecoveryAction.Restore : DesktopRecoveryAction.Discard;
        Pending = false;
        Waiting = false;
        _baseline = null;
        _settlingLayout = null;
        // Window placement is asynchronous; let apps process DPI/size messages before
        // taking another idle snapshot of the restored desktop.
        _idleLayout = null;
        return result;
    }

    public void Reset()
    {
        Pending = false;
        Waiting = false;
        _baseline = _settlingLayout = _idleLayout = null;
    }
}
