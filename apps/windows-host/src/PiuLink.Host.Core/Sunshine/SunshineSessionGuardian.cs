namespace PiuLink.Host.Core.Sunshine;

public enum SunshineSessionGuardState
{
    Ready,
    ActiveStream,
    GracePeriod,
    RecoveryRequired
}

public sealed record SunshineSessionGuardObservation(
    SunshineSessionGuardState State,
    int ApplicationId,
    TimeSpan RetainedFor);

/// <summary>
/// Applies a grace period before a retained Sunshine application can be recovered.
/// A newly connected client or a different application always resets the timer.
/// </summary>
public sealed class SunshineSessionGuardian
{
    public SunshineSessionGuardian(TimeSpan? gracePeriod = null)
    {
        GracePeriod = gracePeriod ?? TimeSpan.FromSeconds(12);
        if (GracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        }
    }

    public TimeSpan GracePeriod { get; }

    private int _retainedApplicationId;
    private DateTimeOffset? _retainedSince;

    public SunshineSessionGuardObservation Observe(
        SunshineOverview overview,
        DateTimeOffset observedAt)
    {
        if (!overview.HasCurrentApplication)
        {
            Reset();
            return new SunshineSessionGuardObservation(
                SunshineSessionGuardState.Ready,
                0,
                TimeSpan.Zero);
        }

        if (overview.HasActiveStreams)
        {
            Reset();
            return new SunshineSessionGuardObservation(
                SunshineSessionGuardState.ActiveStream,
                overview.CurrentApplicationId,
                TimeSpan.Zero);
        }

        if (_retainedSince is null || _retainedApplicationId != overview.CurrentApplicationId)
        {
            _retainedApplicationId = overview.CurrentApplicationId;
            _retainedSince = observedAt;
        }

        var retainedFor = observedAt - _retainedSince.Value;
        if (retainedFor < TimeSpan.Zero)
        {
            retainedFor = TimeSpan.Zero;
            _retainedSince = observedAt;
        }

        return new SunshineSessionGuardObservation(
            retainedFor >= GracePeriod
                ? SunshineSessionGuardState.RecoveryRequired
                : SunshineSessionGuardState.GracePeriod,
            overview.CurrentApplicationId,
            retainedFor);
    }

    public void Reset()
    {
        _retainedApplicationId = 0;
        _retainedSince = null;
    }
}
