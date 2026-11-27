namespace PiuLink.Client.Core.Streaming;

public static class StreamReconnectPolicy
{
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    public static bool ShouldReconnect(int exitCode, bool userRequestedExit, int attempt) =>
        exitCode != 0 && !userRequestedExit && attempt >= 0 && attempt < Delays.Length;

    public static TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 0 || attempt >= Delays.Length)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        return Delays[attempt];
    }
}
