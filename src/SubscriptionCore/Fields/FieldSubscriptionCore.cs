using System.Collections.Immutable;
using ResourceManager.ReadWrite;

namespace ResourceManager.Subscription;

/// <summary>External subscriptions use the read/write core's exact device fields.
/// Validation is metadata-only; the supplied backend owns demand and notification.</summary>
public sealed class FieldSubscriptionCore(ReadWriteCore core, IFieldSubscriptionBackend backend)
{
    private readonly ReadWriteCore core = core ?? throw new ArgumentNullException(nameof(core));
    private readonly IFieldSubscriptionBackend backend = backend ?? throw new ArgumentNullException(nameof(backend));

    /// <summary>One named fresh query through the normal subscription worker.
    /// Empty or failed fields are delivered as such; no cached fallback.</summary>
    public async Task<ImmutableArray<FieldReading>> FetchOnceAsync(string holder,
        ImmutableArray<FieldReference> fields, CancellationToken cancellationToken = default)
    {
        var value = await SubscriptionSingleFetch.ReadAsync(SubscribeAsync(holder, fields, SubscriptionSingleFetch.Interval,
            new FieldSubscriptionOptions { SamplingMode = FieldSubscriptionSamplingMode.FreshOnTrigger },
            cancellationToken), static _ => true, cancellationToken).ConfigureAwait(false);
        return value.IsDefault ? [] : value;
    }

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
    {
        Validate(subscriptionId, fields, interval);
        return backend.Acquire(subscriptionId, fields, interval);
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(subscriptionId, fields, interval);
        return backend.SubscribeAsync(subscriptionId, fields, interval, cancellationToken);
    }

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Validate(subscriptionId, fields, interval);
        return backend.Acquire(subscriptionId, fields, interval, options);
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Validate(subscriptionId, fields, interval);
        return backend.SubscribeAsync(subscriptionId, fields, interval, options, cancellationToken);
    }

    private void Validate(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one field.", nameof(fields));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var catalog = core.Catalog;
        foreach (var field in fields) _ = catalog.Require(field);
    }
}
