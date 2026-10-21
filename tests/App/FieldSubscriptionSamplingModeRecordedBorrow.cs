using System.Collections.Immutable;
using System.Threading.Channels;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Publications;

namespace Resource_Manager_APP.Tests;

internal sealed class FieldSubscriptionSamplingModeRecordedBorrow : IReadWriteCollectionBorrow, IReadWriteBackend
{
    private readonly object gate = new();
    private readonly Dictionary<FieldReference, FieldReading> current = [];
    private ImmutableArray<FieldReference> activeFields = [];
    private int collectionCount;

    internal FieldReference Passive { get; } = new("sampling-mode-recorded", "passive");
    internal FieldReference QueryA { get; } = new("sampling-mode-recorded", "query-a");
    internal FieldReference QueryB { get; } = new("sampling-mode-recorded", "query-b");

    internal Channel<ImmutableArray<FieldReference>> CollectionCalls { get; } =
        Channel.CreateUnbounded<ImmutableArray<FieldReference>>();
    internal Channel<ImmutableArray<FieldReference>> BindingCalls { get; } =
        Channel.CreateUnbounded<ImmutableArray<FieldReference>>();
    internal Channel<FieldReading> CachedReads { get; } = Channel.CreateUnbounded<FieldReading>();
    internal Channel<ImmutableArray<FieldReference>> DemandChanges { get; } =
        Channel.CreateUnbounded<ImmutableArray<FieldReference>>();

    internal Func<FieldReference, FieldCollectionBinding>? BindOverride { get; set; }
    internal Func<ImmutableArray<FieldReference>, IMetricPublicationPermit, CancellationToken,
        ValueTask<FieldCollectionResult>>? CollectionBehavior { get; set; }

    public IReadWriteBackend Backend => this;
    public SamplingPublicationSignal Publications { get; } = new();
    public ReadWriteCatalog Catalog { get; }
    internal int CollectionCount => Volatile.Read(ref collectionCount);
    internal ImmutableArray<FieldReference> ActiveFields { get { lock (gate) return activeFields; } }

    internal FieldSubscriptionSamplingModeRecordedBorrow()
    {
        var fields = new[] { Passive, QueryA, QueryB };
        Catalog = new([new("sampling-mode-recorded", "recorded", null)], fields.Select(field =>
            new FieldCapability(field, new(field.FieldName, "text", "string", null),
                new(true, null, []), OperationSupport.Unsupported("recorded read only"), "recorded")));
        foreach (var field in fields) SetCurrent(field, "initial-current");
    }

    public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields)
    {
        BindingCalls.Writer.TryWrite(fields);
        return [.. fields.Select(field =>
        {
            _ = Catalog.Require(field);
            return BindOverride?.Invoke(field)
                ?? new FieldCollectionBinding(field, ["shared-recorded-item"], [$"publication-{field.FieldName}"], SourceKind(field));
        })];
    }

    internal FieldCollectionSourceKind SourceKind(FieldReference field)
        => field == Passive ? FieldCollectionSourceKind.CurrentValue : FieldCollectionSourceKind.SourceQuery;

    internal bool IsSourceQuery(FieldReference field) => SourceKind(field) == FieldCollectionSourceKind.SourceQuery;

    public void SynchronizeDemand(ImmutableArray<FieldReference> fields)
    {
        lock (gate) activeFields = fields;
        DemandChanges.Writer.TryWrite(fields);
    }

    public async ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
        IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref collectionCount);
        CollectionCalls.Writer.TryWrite(fields);
        if (CollectionBehavior is { } behavior)
            return await behavior(fields, permit, cancellationToken);

        var readings = fields.Select(field => IsSourceQuery(field)
            ? CreateReading(field, $"sample-{CollectionCount}")
            : PeekCurrent(field)).ToImmutableArray();
        return Publish(permit, readings, [.. fields.Where(IsSourceQuery)]);
    }

    internal FieldReading CreateReading(FieldReference field, string value,
        DateTimeOffset? observedAt = null)
        => new(field, FieldObservationStatus.Current, new(Text: value),
            observedAt ?? DateTimeOffset.UtcNow, "recorded-source");

    internal void SetCurrent(FieldReference field, string value)
        => SetCurrent(CreateReading(field, value));

    internal void SetCurrent(FieldReading reading)
    {
        lock (gate) current[reading.Field] = reading;
    }

    internal FieldReading PeekCurrent(FieldReference field)
    {
        lock (gate)
            return current.TryGetValue(field, out var reading) ? reading
                : new(field, FieldObservationStatus.Unavailable, null, null, "recorded-source");
    }

    internal FieldCollectionResult Publish(IMetricPublicationPermit permit,
        ImmutableArray<FieldReading> readings, ImmutableArray<FieldReference> queriedFields)
    {
        var committed = permit.TryPublish(() =>
        {
            lock (gate)
                foreach (var reading in readings) current[reading.Field] = reading;
            Publications.Publish(["sampling-mode-recorded"]);
        });
        return new(readings, committed, queriedFields);
    }

    public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Catalog);

    public FieldReading ReadCached(FieldReference field)
    {
        var reading = PeekCurrent(field);
        CachedReads.Writer.TryWrite(reading);
        return reading;
    }

    public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("The recorded test uses the explicit original collection borrow.");

    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("The recorded test scope is read only.");

    public void Dispose()
    {
        CollectionCalls.Writer.TryComplete();
        BindingCalls.Writer.TryComplete();
        CachedReads.Writer.TryComplete();
        DemandChanges.Writer.TryComplete();
    }
}
