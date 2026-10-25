using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;

internal static class NativeFieldDeliveryConsumer
{
    internal static async Task RunAsync()
    {
        const string sourceKey = "recorded-selection-1";
        const string alphaItem = "recorded.alpha";
        const string betaItem = "recorded.beta";
        const string alphaQueryKey = "query.alpha";
        const string betaQueryKey = "query.beta";
        var alpha = new FieldReference("recorded-device", "alpha_count_actual");
        var beta = new FieldReference("recorded-device", "beta_count_actual");
        var fields = ImmutableArray.Create(alpha, beta);
        var backend = new RecordedBackend(alpha, beta);
        var delivery = new NativeFieldSubscriptionDelivery();
        var bindings = ImmutableArray.Create(
            new FieldCollectionBinding(alpha, [alphaItem], [alphaQueryKey], FieldCollectionSourceKind.SourceQuery),
            new FieldCollectionBinding(beta, [betaItem], [betaQueryKey], FieldCollectionSourceKind.CurrentValue));
        var registration = delivery.Register(sourceKey, backend, fields, bindings);
        await using var stream = registration.ReadAllAsync().GetAsyncEnumerator();

        var firstTriggers = Triggers(sourceKey, alphaItem, betaItem, sequence: 1, due: 1_000);
        var queriedKeys = new HashSet<string>([alphaQueryKey, betaQueryKey], StringComparer.Ordinal);
        var firstCapture = delivery.CaptureCompleted(firstTriggers, queriedKeys, committed: true);
        Require(firstCapture.Committed && firstCapture.Readings.Select(static reading => reading.Field).SequenceEqual(fields)
            && firstCapture.QueriedFields.SequenceEqual([alpha]),
            "capture snapshots exact fields and only the actually queried publication key");
        Require(backend.ReadCachedFields.SequenceEqual(fields) && backend.CollectionCalls == 0,
            "capture reads the recorded original current without starting a producer");

        var firstRound = stream.MoveNextAsync().AsTask();
        delivery.Deliver([firstTriggers[0]], firstCapture);
        Require(!firstRound.IsCompleted, "one item trigger cannot complete a two-item round");
        delivery.Deliver([firstTriggers[1]], firstCapture);
        Require(await firstRound, "all item triggers complete the round");
        Require(stream.Current.Select(static reading => reading.Field).SequenceEqual(fields)
            && stream.Current[0].Status == FieldObservationStatus.Current
            && stream.Current[0].Value?.Integer == MetricInteger.Parse("101")
            && stream.Current[1].Status == FieldObservationStatus.Unsupported
            && stream.Current[1].Value is null,
            "fresh result preserves queried source data while passive current-only data is unsupported");

        var readsBeforeReject = backend.ReadCachedFields.Count;
        var rejectedTriggers = Triggers(sourceKey, alphaItem, betaItem, sequence: 2, due: 2_000);
        var rejectedCapture = delivery.CaptureCompleted(rejectedTriggers, queriedKeys, committed: false);
        Require(!rejectedCapture.Committed && rejectedCapture.Readings.IsEmpty
            && rejectedCapture.QueriedFields.SequenceEqual([alpha])
            && backend.ReadCachedFields.Count == readsBeforeReject,
            "rejected publication retains query evidence without reading backend cache");
        var rejectedRound = stream.MoveNextAsync().AsTask();
        delivery.Deliver(rejectedTriggers, rejectedCapture);
        Require(await rejectedRound && stream.Current[0].Status == FieldObservationStatus.Unavailable
            && stream.Current[0].Value is null && stream.Current[1].Status == FieldObservationStatus.Unsupported,
            "rejected fresh capture delivers unavailable source evidence, never an old cached value");

        delivery.RetireAll();
        Require(!await stream.MoveNextAsync(), "RetireAll closes the original registration stream");
        var replacement = delivery.Register(sourceKey, backend, fields, bindings);
        registration.Dispose();
        await using var replacementStream = replacement.ReadAllAsync().GetAsyncEnumerator();
        var replacementTriggers = Triggers(sourceKey, alphaItem, betaItem, sequence: 1, due: 3_000);
        var replacementCapture = delivery.CaptureCompleted(replacementTriggers, queriedKeys, committed: true);
        var replacementRound = replacementStream.MoveNextAsync().AsTask();
        delivery.Deliver(replacementTriggers, replacementCapture);
        Require(await replacementRound && replacementStream.Current.Length == 2,
            "a retired source key can register and deliver a new complete round");
        delivery.RetireAll();

        Console.WriteLine("[NATIVE-FIELD-DELIVERY-PACKAGE] recorded two-item trigger rounds, exact query keys, passive freshness, rejected commit and retire/re-register.");
    }

    private static ImmutableArray<NativeItemSamplingSubscriptionTrigger<string>> Triggers(
        string sourceKey, string alphaItem, string betaItem, ulong sequence, ulong due)
        =>
        [
            new(sourceKey, alphaItem, due, sequence, "recorded-request", IsFresh: true),
            new(sourceKey, betaItem, due, sequence, "recorded-request", IsFresh: true)
        ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class RecordedBackend(FieldReference alpha, FieldReference beta) : IReadWriteBackend
    {
        public ReadWriteCatalog Catalog { get; } = new(
            [new("recorded-device", "recorded-source", null)],
            [
                Capability(alpha, "alpha_count_actual"),
                Capability(beta, "beta_count_actual")
            ]);
        internal List<FieldReference> ReadCachedFields { get; } = [];
        internal int CollectionCalls { get; private set; }

        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Catalog);

        public FieldReading ReadCached(FieldReference field)
        {
            ReadCachedFields.Add(field);
            var value = field == alpha ? "101" : field == beta ? "202"
                : throw new ArgumentException("Unexpected recorded field.", nameof(field));
            return new(field, FieldObservationStatus.Current, new(Integer: MetricInteger.Parse(value)),
                DateTimeOffset.Parse("2026-10-09T12:00:00Z"), "recorded-source");
        }

        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(
            ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectionCalls++;
            throw new InvalidOperationException("Delivery projection must not start a source query.");
        }

        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new FieldWriteResult(field, "unsupported", "read-only"));

        private static FieldCapability Capability(FieldReference field, string name)
            => new(field, new(name, "count", "unsigned64"),
                new(true, null, []), OperationSupport.Unsupported("read-only"), "recorded-source");
    }
}
