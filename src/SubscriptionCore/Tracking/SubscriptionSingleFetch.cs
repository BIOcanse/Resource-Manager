namespace ResourceManager.Subscription;

/// <summary>A one-shot consumer of an ordinary named subscription. Enumeration
/// owns the demand; completing, cancellation and failures all dispose it.
/// Releasing this holder is not a drain barrier for shared sampling already in flight.</summary>
public static class SubscriptionSingleFetch
{
    // A single fetch requests a prompt initial sample, then ends. One second is
    // the normal monitoring cadence if it must wait for a shared sampling round.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    public static async Task<T?> ReadAsync<T>(IAsyncEnumerable<T> subscription,
        Func<T, bool> isNewSample, CancellationToken cancellationToken)
    {
        await foreach (var value in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
            if (isNewSample(value)) return value;
        return default;
    }
}
