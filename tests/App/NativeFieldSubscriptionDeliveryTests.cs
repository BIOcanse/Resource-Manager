using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class NativeFieldSubscriptionDeliveryTests
{
    private static readonly FieldReference QueryA = new("delivery-recorded", "query-a");
    private static readonly FieldReference QueryB = new("delivery-recorded", "query-b");
    private static readonly FieldReference Passive = new("delivery-recorded", "passive");
    private const string SourceKey = "delivery-source";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HundredSelectedFieldsReadOneBatchPerBackendAcrossDeliveryRegistrations(bool fresh)
    {
        var fields = Enumerable.Range(0, 100).Select(index => new FieldReference("delivery-recorded", $"field-{index}"))
            .ToImmutableArray();
        var backend = new RecordedBackend(fields);
        var delivery = new NativeFieldSubscriptionDelivery();
        var bindings = fields.Select(field => new FieldCollectionBinding(field, ["batch-item"], ["batch-publication"],
            FieldCollectionSourceKind.SourceQuery)).ToImmutableArray();
        using var registration = delivery.Register(SourceKey, backend, fields, bindings);
        using var overlapping = delivery.Register("overlapping", backend, fields[..50], bindings[..50]);
        backend.Publish([.. fields.Select(field => Current(field, field.FieldName))]);
        var triggers = Triggers(1, fresh, ("batch-item", SourceKey), ("batch-item", "overlapping"));

        var result = fresh ? delivery.CaptureCompleted(triggers,
            new HashSet<string>(["batch-publication"], StringComparer.Ordinal), committed: true) : null;
        delivery.Deliver(triggers, result);

        var values = await ReadSingleAsync(registration);
        Assert.Equal(fields, values.Select(static value => value.Field));
        Assert.All(values, value => Assert.Equal(value.Field.FieldName, value.Value?.Text));
        Assert.Equal(50, (await ReadSingleAsync(overlapping)).Length);
        Assert.Equal(1, backend.BatchReadCount);
        Assert.Equal(100, backend.BatchFieldCount);
        Assert.Equal(0, backend.ReadCount);
    }

    [Fact]
    public async Task IncompleteCurrentRoundDoesNotReadBeforeAllBoundItemsComplete()
    {
        var backend = new RecordedBackend();
        var delivery = new NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register(SourceKey, backend, [QueryA, QueryB], [Bindings()[0], Bindings()[1]]);
        backend.Publish(Current(QueryA, "current-a"), Current(QueryB, "current-b"));

        delivery.Deliver(Triggers(1, false, ("item-a", SourceKey)), null);
        Assert.Equal(0, backend.BatchReadCount);
        Assert.Equal(0, backend.ReadCount);
        delivery.Deliver(Triggers(1, false, ("item-b", SourceKey)), null);

        Assert.Equal(2, (await ReadSingleAsync(registration)).Length);
        Assert.Equal(1, backend.BatchReadCount);
        Assert.Equal(2, backend.BatchFieldCount);
    }

    [Fact]
    public async Task BatchCurrentDeliveryKeepsValidFieldsWhenOneBindingRetires()
    {
        var backend = new RecordedBackend();
        var delivery = new NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register(SourceKey, backend, [QueryA, QueryB], [Bindings()[0], Bindings()[1]]);
        backend.Publish(Current(QueryA, "old-a"), Current(QueryB, "current-b"));
        backend.RemoveBinding(QueryA);
        delivery.Deliver(Triggers(1, false, ("coalesced", SourceKey)), null);

        var values = await ReadSingleAsync(registration);
        Assert.Equal(FieldObservationStatus.Unavailable, values[0].Status);
        Assert.Null(values[0].Value);
        Assert.Equal("current-b", values[1].Value?.Text);
        Assert.Equal(1, backend.BatchReadCount);
        Assert.Equal(1, backend.BatchFieldCount);
        Assert.Equal(0, backend.ReadCount);
    }

    [Fact]
    public async Task FreshRoundWaitsForEveryBoundItemAndUsesOnlyQueriedPublicationKeys()
    {
        var backend = new RecordedBackend();
        var delivery = new NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register(SourceKey, backend, [QueryA, QueryB, Passive], Bindings());
        var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        backend.Publish(
            Current(QueryA, "query-a-new", capturedAt),
            Current(QueryB, "query-b-new", capturedAt),
            Current(Passive, "passive-current", capturedAt));
        var triggers = Triggers(11, fresh: true,
            ("item-a", SourceKey), ("item-b", SourceKey), ("item-passive", SourceKey));
        var result = delivery.CaptureCompleted(triggers,
            new HashSet<string>(["publication-a", "publication-b"], StringComparer.Ordinal), committed: true);

        Assert.True(result.Committed);
        Assert.Equal(new[] { QueryA, QueryB }, result.QueriedFields);
        Assert.Equal(3, result.Readings.Length);
        var reader = registration.ReadAllAsync().GetAsyncEnumerator();
        ImmutableArray<FieldReading> delivered;
        await using (reader)
        {
            delivery.Deliver(triggers.Take(1).ToImmutableArray(), result, freshOnly: true);
            var pending = reader.MoveNextAsync().AsTask();
            Assert.False(pending.IsCompleted);
            delivery.Deliver(triggers.Skip(1).ToImmutableArray(), result, freshOnly: true);
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            delivered = reader.Current;
        }

        Assert.Equal(new[] { QueryA, QueryB, Passive }, delivered.Select(static reading => reading.Field).ToArray());
        Assert.Equal(FieldObservationStatus.Current, delivered[0].Status);
        Assert.Equal("query-a-new", delivered[0].Value?.Text);
        Assert.Equal(capturedAt, delivered[0].ObservedAt);
        Assert.Equal(FieldObservationStatus.Current, delivered[1].Status);
        Assert.Equal("query-b-new", delivered[1].Value?.Text);
        Assert.Equal(FieldObservationStatus.Unsupported, delivered[2].Status);
        Assert.Null(delivered[2].Value);
    }

    [Fact]
    public async Task UncommittedAndUnqueriedFreshReadsNeverFallBackToTheOldCurrent()
    {
        var backend = new RecordedBackend();
        backend.Publish(Current(QueryA, "old-a"), Current(QueryB, "old-b"), Current(Passive, "passive"));
        backend.ResetReadCount();
        var delivery = new NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register(SourceKey, backend, [QueryA, Passive],
            [Bindings()[0], Bindings()[2]]);
        var triggers = Triggers(21, fresh: true, ("item-a", SourceKey), ("item-passive", SourceKey));

        var rejected = delivery.CaptureCompleted(triggers,
            new HashSet<string>(["publication-a"], StringComparer.Ordinal), committed: false);
        Assert.False(rejected.Committed);
        Assert.Empty(rejected.Readings);
        Assert.Equal(new[] { QueryA }, rejected.QueriedFields);
        Assert.Equal(0, backend.ReadCount);
        delivery.Deliver(triggers, rejected, freshOnly: true);
        var rejectedValues = await ReadSingleAsync(registration);
        Assert.Equal(FieldObservationStatus.Unavailable, rejectedValues[0].Status);
        Assert.Null(rejectedValues[0].Value);
        Assert.Equal(FieldObservationStatus.Unsupported, rejectedValues[1].Status);
        Assert.Null(rejectedValues[1].Value);

        var unqueriedTriggers = Triggers(22, fresh: true, ("item-a", SourceKey), ("item-passive", SourceKey));
        var acceptedWithoutQuery = delivery.CaptureCompleted(unqueriedTriggers,
            new HashSet<string>(StringComparer.Ordinal), committed: true);
        Assert.True(acceptedWithoutQuery.Committed);
        Assert.Empty(acceptedWithoutQuery.QueriedFields);
        Assert.Equal(2, acceptedWithoutQuery.Readings.Length);
        delivery.Deliver(unqueriedTriggers, acceptedWithoutQuery, freshOnly: true);
        var unqueriedValues = await ReadSingleAsync(registration);
        Assert.Equal(FieldObservationStatus.Unavailable, unqueriedValues[0].Status);
        Assert.Null(unqueriedValues[0].Value);
        Assert.Equal(FieldObservationStatus.Unsupported, unqueriedValues[1].Status);
    }

    [Fact]
    public async Task CurrentDeliveryKeepsOnlyLatestCompleteRoundAndCancellationRejectsBufferedRound()
    {
        var backend = new RecordedBackend();
        var delivery = new NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register(SourceKey, backend, [QueryA], [Bindings()[0]]);
        backend.Publish(Current(QueryA, "round-one"));
        delivery.Deliver(Triggers(31, fresh: false, ("item-a", SourceKey)), null);
        backend.Publish(Current(QueryA, "round-two"));
        delivery.Deliver(Triggers(32, fresh: false, ("item-a", SourceKey)), null);

        Assert.Equal("round-two", (await ReadSingleAsync(registration))[0].Value?.Text);

        backend.Publish(Current(QueryA, "buffered-before-cancel"));
        delivery.Deliver(Triggers(33, fresh: false, ("item-a", SourceKey)), null);
        using var canceled = new CancellationTokenSource();
        await using (var canceledReader = registration.ReadAllAsync(canceled.Token).GetAsyncEnumerator(canceled.Token))
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                _ = await canceledReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            });
        }
        Assert.Equal("buffered-before-cancel", (await ReadSingleAsync(registration))[0].Value?.Text);

        registration.Dispose();
        await using var retiredReader = registration.ReadAllAsync().GetAsyncEnumerator();
        Assert.False(await retiredReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RetireAllClosesChannelsAndAllowsANewRegistration()
    {
        var backend = new RecordedBackend();
        var delivery = new NativeFieldSubscriptionDelivery();
        var first = delivery.Register(SourceKey, backend, [QueryA], [Bindings()[0]]);
        delivery.Deliver(Triggers(41, fresh: false, ("item-a", SourceKey)), null);
        delivery.RetireAll();
        await using (var firstReader = first.ReadAllAsync().GetAsyncEnumerator())
            Assert.False(await firstReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        using var replacement = delivery.Register(SourceKey, backend, [QueryA], [Bindings()[0]]);
        backend.Publish(Current(QueryA, "after-retirement"));
        delivery.Deliver(Triggers(42, fresh: false, ("item-a", SourceKey)), null);
        Assert.Equal("after-retirement", (await ReadSingleAsync(replacement))[0].Value?.Text);
    }

    [Fact]
    public async Task CoalescedTrackerItemCompletesTheRegisteredFieldRound()
    {
        using var owner = CreateRoleOwner();
        var tracker = CreateTracker(owner, coalesce: true);
        var coalescedItem = Assert.Single(tracker.GetDeliveryItemIds("alpha,beta"));
        var backend = new RecordedBackend();
        backend.Publish(Current(QueryA, "coalesced-a"), Current(QueryB, "coalesced-b"));
        var delivery = new NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register(SourceKey, backend, [QueryA, QueryB],
            [Bindings()[0], Bindings()[1]]);

        delivery.Deliver(Triggers(51, fresh: false, (coalescedItem, SourceKey)), null);
        var values = await ReadSingleAsync(registration);
        Assert.Equal(new[] { QueryA, QueryB }, values.Select(static reading => reading.Field).ToArray());
        Assert.Equal(new[] { "coalesced-a", "coalesced-b" },
            values.Select(static reading => reading.Value?.Text).ToArray());
    }

    [Fact]
    public void TrackerExposesNormalizedNativeItemsAndValidatesCurrentRolePeriod()
    {
        using var owner = CreateRoleOwner();
        var perItem = CreateTracker(owner, coalesce: false);
        var coalesced = CreateTracker(owner, coalesce: true);

        Assert.Equal(new[] { "alpha", "beta" }, perItem.GetDeliveryItemIds(" beta,alpha, alpha ").ToArray());
        var nativeCaptureItem = Assert.Single(coalesced.GetDeliveryItemIds("beta,alpha"));
        Assert.NotEqual("alpha", nativeCaptureItem);
        Assert.NotEqual("beta", nativeCaptureItem);
        perItem.ValidatePeriod(TimeSpan.FromMilliseconds(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => perItem.ValidatePeriod(TimeSpan.FromMilliseconds(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => perItem.ValidatePeriod(TimeSpan.FromTicks(
            7 * TimeSpan.TicksPerMillisecond + 1)));
    }

    private static NativeItemSamplingSubscriptionTracker<string, SubscriptionRunPermit> CreateTracker(
        StandaloneSamplingSubscriptionSessionOwner owner, bool coalesce)
        => new(owner, 1, static _ => "recorded-source",
            static request => request.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            static (items, _) => string.Join(',', items), coalesce);

    private static StandaloneSamplingSubscriptionSessionOwner CreateRoleOwner()
    {
        var settings = new SubscriptionRoleSettings(1, 10, 7, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(),
            Generation = 1,
            MaximumSourceCount = 8,
            MaximumItemCount = 8,
            MaximumMembershipCount = 16,
            MaximumDueItemCount = 8,
            MaximumSourceViewCount = 8,
            MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 10,
            MinimumIntervalMilliseconds = 7,
            ActiveTtlMilliseconds = 3000,
            MaximumFutureSkewMilliseconds = 1000
        };
        return new(settings, in configuration, 1);
    }

    private static ImmutableArray<FieldCollectionBinding> Bindings() =>
    [
        new(QueryA, ["item-a"], ["publication-a"], FieldCollectionSourceKind.SourceQuery),
        new(QueryB, ["item-b"], ["publication-b"], FieldCollectionSourceKind.SourceQuery),
        new(Passive, ["item-passive"], ["publication-passive"], FieldCollectionSourceKind.CurrentValue)
    ];

    private static ImmutableArray<NativeItemSamplingSubscriptionTrigger<string>> Triggers(ulong sequence,
        bool fresh, params (string Item, string Source)[] values)
        => [.. values.Select(value => new NativeItemSamplingSubscriptionTrigger<string>(value.Source,
            value.Item, ScheduledDueMilliseconds: 100, Sequence: sequence, Request: "recorded-request", IsFresh: fresh))];

    private static FieldReading Current(FieldReference field, string value,
        DateTimeOffset? observedAt = null)
        => new(field, FieldObservationStatus.Current, new(Text: value), observedAt ?? DateTimeOffset.UnixEpoch, "recorded-source");

    private static async Task<ImmutableArray<FieldReading>> ReadSingleAsync(
        NativeFieldSubscriptionDelivery.Registration registration)
    {
        await using var reader = registration.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        return reader.Current;
    }

    private sealed class RecordedBackend : IReadWriteBackend
    {
        private readonly object gate = new();
        private readonly Dictionary<FieldReference, FieldReading> current = [];
        private int reads;

        public RecordedBackend(ImmutableArray<FieldReference> fields = default)
        {
            if (fields.IsDefault) fields = [QueryA, QueryB, Passive];
            Catalog = new([new("delivery-recorded", "recorded", null)],
                fields.Select(field => new FieldCapability(field,
                new(field.FieldName, "text", "string"), new(true, null, []),
                OperationSupport.Unsupported("recorded read only"), "recorded-source")));
        }

        public ReadWriteCatalog Catalog { get; private set; }
        internal int ReadCount => Volatile.Read(ref reads);
        internal int BatchReadCount { get; private set; }
        internal int BatchFieldCount { get; private set; }
        internal void RemoveBinding(FieldReference field)
            => Catalog = new(Catalog.Devices, Catalog.Fields.Where(capability => capability.Field != field));

        internal void Publish(params FieldReading[] readings)
        {
            lock (gate)
                foreach (var reading in readings) current[reading.Field] = reading;
        }

        internal void ResetReadCount() => Volatile.Write(ref reads, 0);

        public FieldReading ReadCached(FieldReference field)
        {
            Interlocked.Increment(ref reads);
            lock (gate)
                return current.TryGetValue(field, out var reading) ? reading
                    : new(field, FieldObservationStatus.Unavailable, null, null, "recorded-source");
        }

        public ImmutableArray<FieldReading> ReadCachedBatch(ImmutableArray<FieldReference> fields)
        {
            BatchReadCount++;
            BatchFieldCount += fields.Length;
            lock (gate)
                return [.. fields.Select(field => current.TryGetValue(field, out var reading) ? reading
                    : new(field, FieldObservationStatus.Unavailable, null, null, "recorded-source"))];
        }

        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Catalog);

        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The recorded delivery does not collect.");

        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The recorded delivery is read only.");
    }
}
