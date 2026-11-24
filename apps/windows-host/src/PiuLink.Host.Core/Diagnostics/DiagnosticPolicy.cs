namespace PiuLink.Host.Core.Diagnostics;

public enum DiagnosticLevel
{
    Debug = 0,
    Info = 1,
    Err = 2
}

/// <summary>Global diagnostics policy. This intentionally lives in code, not UI/settings.</summary>
public static class DiagnosticPolicy
{
    public const DiagnosticLevel MinimumLevel = DiagnosticLevel.Info;
    public const int RetentionDays = 3;
    public const long MaxFileBytes = 8L * 1024 * 1024;
    public const long MaxTotalBytes = 48L * 1024 * 1024;
    public const int QueueCapacity = 4096;
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);
}
