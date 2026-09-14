namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal sealed class HostManagerDurableTimeSource(TimeProvider timeProvider)
{
    private long lastUtcMilliseconds;

    public ulong NextUtcMilliseconds(ulong minimumUtcMilliseconds = 0)
    {
        if (minimumUtcMilliseconds > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumUtcMilliseconds));
        }

        var observed = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (observed <= 0)
        {
            throw new InvalidOperationException("The current UTC time is outside the Host Manager durable time domain.");
        }

        var minimum = checked((long)minimumUtcMilliseconds);
        while (true)
        {
            var previous = Volatile.Read(ref lastUtcMilliseconds);
            var next = Math.Max(Math.Max(observed, minimum), previous);
            if (Interlocked.CompareExchange(ref lastUtcMilliseconds, next, previous) == previous)
            {
                return checked((ulong)next);
            }
        }
    }

    public DateTimeOffset NextUtc(ulong minimumUtcMilliseconds = 0)
        => DateTimeOffset.FromUnixTimeMilliseconds(
            checked((long)NextUtcMilliseconds(minimumUtcMilliseconds)));
}
