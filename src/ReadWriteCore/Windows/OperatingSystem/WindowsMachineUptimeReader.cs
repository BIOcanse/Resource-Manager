namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Original machine elapsed time and floor seconds. This is not a
/// wall-clock boot timestamp, application uptime or session age.</summary>
public sealed record WindowsMachineUptime(TimeSpan Elapsed, ulong Seconds);

public static class WindowsMachineUptimeReader
{
    public static WindowsMachineUptime? Read()
        => FromTickCountMilliseconds(Environment.TickCount64);

    internal static WindowsMachineUptime? FromTickCountMilliseconds(long milliseconds)
    {
        if (milliseconds < 0) return null;
        try
        {
            // Retain the original provider's conversion/floor, including its
            // elapsed milliseconds used by the product's existing boot estimate.
            var elapsed = TimeSpan.FromMilliseconds(milliseconds);
            return new(elapsed, checked((ulong)Math.Floor(elapsed.TotalSeconds)));
        }
        catch (OverflowException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

