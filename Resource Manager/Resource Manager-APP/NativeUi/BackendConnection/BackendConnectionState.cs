namespace ResourceManager.NativeUi;

internal enum BackendConnectionState
{
    Connecting,
    Ready,
    Degraded,
    Reconnecting,
    ShuttingDown
}

internal static class BackendReconnectPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15)
    ];

    public static TimeSpan GetDelay(int failedAttemptCount)
    {
        if (failedAttemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAttemptCount));
        }

        return Delays[Math.Min(failedAttemptCount, Delays.Length - 1)];
    }
}
