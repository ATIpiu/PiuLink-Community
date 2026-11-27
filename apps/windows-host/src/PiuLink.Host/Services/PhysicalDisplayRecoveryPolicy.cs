namespace PiuLink.Host.Services;

internal enum PhysicalDisplayAction { Wait, Capture, Restore }

/// <summary>Freeze the complete idle topology during streaming, including exclusive physical targets.</summary>
internal sealed class PhysicalDisplayRecoveryPolicy
{
    private string? _baseline, _candidate;
    private long _candidateSince, _quietSince, _lastAttempt;
    private bool _pending, _quiet, _owned;
    private long _preparingUntil;
    public void BeginPreparation(long expiresAt) { _owned = true; _preparingUntil = expiresAt; _quiet = false; }
    // Aborted is recorded only after the paired owner releases this specific request.
    public static bool CanRestoreReleasedSession(string phase, bool? active, bool retained) =>
        phase == "Aborted" && active == false && !retained;

    public static bool ShouldRestore(bool? active, bool engineAlive, bool hasJournal, bool preparing, bool expired,
        bool wasStreaming = false, TimeSpan? disconnectedFor = null) =>
        hasJournal && active != true && (!engineAlive ||
            (active == false && (!preparing || expired) &&
             (!wasStreaming || disconnectedFor >= TimeSpan.FromSeconds(5))));

    public void CaptureBaseline(string layout)
    {
        if (string.IsNullOrWhiteSpace(layout) || _pending) return;
        _baseline = layout;
        _candidate = layout;
    }

    public PhysicalDisplayAction Observe(bool streaming, bool physicalOnly, string layout, long now)
    {
        if (_owned && !streaming && now < _preparingUntil) return PhysicalDisplayAction.Wait;
        if (_owned && (streaming || !physicalOnly))
        {
            if (_baseline != null) _pending = true;
            _candidate = null;
            if (streaming) { _quiet = false; return PhysicalDisplayAction.Wait; }
        }
        if (_pending)
        {
            if (!_quiet) { _quiet = true; _quietSince = now; _lastAttempt = now; }
            if (physicalOnly && layout == _baseline)
            {
                _pending = false; _quiet = false;
                return PhysicalDisplayAction.Wait;
            }
            // Leave time for Sunshine's own reversion and for monitors to wake up.
            if (now - _lastAttempt < 5000) return PhysicalDisplayAction.Wait;
            _lastAttempt = now;
            return PhysicalDisplayAction.Restore;

        }
        if (!physicalOnly) return PhysicalDisplayAction.Wait;
        if (_candidate != layout) { _candidate = layout; _candidateSince = now; }
        if (now - _candidateSince < 3000) return PhysicalDisplayAction.Wait;
        _baseline = layout;
        return PhysicalDisplayAction.Capture;
    }
}
