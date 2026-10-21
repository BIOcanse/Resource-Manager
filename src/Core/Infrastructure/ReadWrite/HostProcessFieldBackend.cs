using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.Subscription;

namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>Host demand/worker adapter over the SDK catalog and the original shared process current slots.</summary>
public sealed class HostProcessFieldBackend(WindowsResourceBreakdownSampler sampler) : IReadWriteBackend, IFieldSubscriptionBackend
{
    public bool SupportsPeriodicDelivery => true;
    private readonly ProcessFieldReadWriteBackend backend = new(sampler.ReadProcessFieldParts, sampler.ReadProcessFieldPart,
        () => sampler.ProcessFieldRevision, async (names, ct) =>
            (await sampler.CaptureProcessFieldsAsync(Request(names), ct)).ProcessFields
                ?? ProcessMetricFieldSnapshot.Empty(names.ToHashSet(StringComparer.OrdinalIgnoreCase)));
    public ReadWriteCatalog Catalog => backend.Catalog;
    public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken) => backend.DiscoverAsync(cancellationToken);
    public FieldReading ReadCached(FieldReference field) => backend.ReadCached(field);
    public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
    {
        _ = SelectOwned(fields);
        return backend.CollectAsync(fields, cancellationToken);
    }
    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken)
        => backend.WriteAsync(field, value, cancellationToken);
    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
        => Acquire(subscriptionId, fields, interval, FieldSubscriptionOptions.Default);
    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selected = SelectOwned(fields);
        return sampler.AcquireFieldSubscription(subscriptionId, fields, [.. selected], interval, options, this);
    }
    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken cancellationToken)
        => SubscribeAsync(subscriptionId, fields, interval, FieldSubscriptionOptions.Default, cancellationToken);
    public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var selected = SelectOwned(fields);
        var currentBackend = new ProcessCurrentReadBackend(this, sampler, Catalog, selected);
        await foreach (var readings in sampler.SubscribeFieldAsync(subscriptionId, fields, [.. selected],
            interval, options, currentBackend, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return readings;
    }
    private ImmutableArray<ProcessFieldReadWriteBackend.Selection> SelectOwned(
        ImmutableArray<FieldReference> fields)
    {
        var selected = backend.Select(fields);
        if (selected.Any(static selection =>
                selection.Definition.DatasetId == ProcessMetricDatasetIds.ObservedCpuResidency))
            throw new ArgumentException(
                "CPU residency is owned by HostCpuResidencyFieldBackend, not HostProcessFieldBackend.",
                nameof(fields));
        return selected;
    }

    // The hosted publication callback runs under the original current gate. Snapshot the
    // catalog before registration and read the same slots directly to avoid lock inversion
    // with ProcessFieldReadWriteBackend's catalog refresh lock.
    private sealed class ProcessCurrentReadBackend(
        HostProcessFieldBackend owner,
        WindowsResourceBreakdownSampler sampler,
        ReadWriteCatalog catalog,
        ImmutableArray<ProcessFieldReadWriteBackend.Selection> selections) : IReadWriteBackend
    {
        private readonly FrozenDictionary<FieldReference, ProcessFieldReadWriteBackend.Selection> bindings
            = selections.ToFrozenDictionary(static selection => selection.Field);

        public ReadWriteCatalog Catalog { get; } = catalog;

        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
            => owner.DiscoverAsync(cancellationToken);

        public FieldReading ReadCached(FieldReference field)
        {
            if (!bindings.TryGetValue(field, out var selection))
                throw new ArgumentException("Unknown process creation field.", nameof(field));
            return ProcessFieldReadWriteBackend.Project(selection,
                sampler.ReadProcessFieldPart(selection.Definition.DatasetId));
        }

        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(
            ImmutableArray<FieldReference> fields,
            CancellationToken cancellationToken)
        {
            return owner.CollectAsync(fields, cancellationToken);
        }

        public ValueTask<FieldWriteResult> WriteAsync(
            FieldReference field,
            FieldValue value,
            CancellationToken cancellationToken)
            => owner.WriteAsync(field, value, cancellationToken);
    }

    private static ResourceBreakdownSampleRequest Request(IEnumerable<string> names)
        => ResourceBreakdownSampleRequest.ForResourceTable(names.Distinct(StringComparer.Ordinal).ToArray(), new Dictionary<string, string>());
}
