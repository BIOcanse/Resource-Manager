using System.Collections.Immutable;
using ResourceManager.ReadWrite;

namespace ResourceManager.Subscription;

/// <summary>Adapts the existing demand owner and publication stream. It owns no
/// second sampling cache. Disposing a lease or enumerator ends only its demand.</summary>
public interface IFieldSubscriptionBackend
{
    /// <summary>True when the original stream delivers complete rounds at its
    /// subscription period. Legacy publication adapters keep their existing notification contract.</summary>
    bool SupportsPeriodicDelivery => false;

    IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval);
    IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken cancellationToken);

    /// <summary>Explicit periodic sampling policy. Publication-only adapters must
    /// implement this contract before advertising support for either mode.</summary>
    IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options)
        => throw new NotSupportedException("This adapter has no periodic sampling policy binding.");

    IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This adapter has no periodic sampling policy binding.");
}
