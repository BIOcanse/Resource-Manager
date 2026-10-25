using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class SubscriptionSamplingModeConsumer
{
    private const string DeviceIdentity = "recorded-sampling-device";
    private const string CollectionItemId = "recorded-sampling-item";
    private const string SourceId = "recorded.sampling-mode-source";
    private static readonly FieldReference QueryField = new(DeviceIdentity, "recorded_query_value");
    private static readonly FieldReference PassiveField = new(DeviceIdentity, "recorded_passive_value");
    private static readonly ImmutableArray<FieldReference> FreshFields = [QueryField, PassiveField];

    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        await VerifyFreshQueryPassiveFieldAndStopAsync(failures, timeout.Token);
        await VerifyHighestFrequencyUsesLatestCurrentAsync(failures, timeout.Token);
        Require(failures.IsEmpty, "the recorded source completed without worker failures");
        Console.WriteLine("SubscriptionCore sampling-mode consumer passed: fresh query evidence, passive-field rejection, highest-frequency current delivery, stop and retained direct owner.");
    }

    private static async Task VerifyFreshQueryPassiveFieldAndStopAsync(
        ConcurrentQueue<SubscriptionWorkerFailure> failures, CancellationToken cancellationToken)
    {
        using var owner = new RecordedSamplingOwner(pauseFirstBorrowQuery: true);
        var borrow = owner.BorrowSubscriptionCapture();
        var bindings = borrow.Bind(FreshFields).ToDictionary(static item => item.Field);
        Require(bindings[QueryField].SourceKind == FieldCollectionSourceKind.SourceQuery
            && bindings[PassiveField].SourceKind == FieldCollectionSourceKind.CurrentValue,
            "the public binding distinguishes a queryable field from a passive current field");

        await using var composition = CreateComposition(borrow, failures.Enqueue);
        await composition.StartAsync(cancellationToken);
        const int intervalMilliseconds = 160;
        var stream = composition.SubscribeAsync("recorded-fresh-mode", FreshFields,
            TimeSpan.FromMilliseconds(intervalMilliseconds),
            new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        IDisposable? fastDemand = null;
        try
        {
            Require(owner.BorrowQueryCount == 0, "starting the native composition does not query before subscription demand");
            var pending = stream.MoveNextAsync().AsTask();
            var querySequence = await owner.FirstBorrowQueryStarted.Task.WaitAsync(cancellationToken);
            Require(querySequence == 1 && owner.BorrowQueryCount == 1,
                "the first FreshOnTrigger event enters the original borrowed source query");
            Require(owner.Backend.ReadCached(QueryField).Value?.Number == 5d,
                "the cached seed remains visible while the triggered query is held before publication");

            owner.SetNextQueryValue(42);
            owner.ReleaseFirstBorrowQuery();
            Require(await pending.WaitAsync(cancellationToken), "the completed fresh query is delivered");

            var queryReading = stream.Current.Single(static reading => reading.Field == QueryField);
            var passiveReading = stream.Current.Single(static reading => reading.Field == PassiveField);
            var queryResult = owner.GetBorrowResult(querySequence);
            Require(queryResult.Committed && !queryResult.QueriedFields.IsDefault
                && queryResult.QueriedFields.SequenceEqual([QueryField]),
                "the public collection result marks the committed source query field exactly");
            Require(queryResult.Readings.Single(static reading => reading.Field == QueryField).Value?.Number == 42d
                && queryReading.Status == FieldObservationStatus.Current
                && queryReading.Value?.Number == 42d
                && owner.Backend.ReadCached(QueryField) == queryReading,
                "FreshOnTrigger returns the value read after the trigger and committed to the same owner current");
            Require(passiveReading.Status == FieldObservationStatus.Unsupported && passiveReading.Value is null
                && owner.Backend.ReadCached(PassiveField).Status == FieldObservationStatus.Current,
                "a committed collection does not make a passive current field fresh evidence");

            // Keep a source-query demand alive while StopAsync retires the worker.
            fastDemand = composition.Acquire("recorded-stop-keeps-demand", [QueryField],
                TimeSpan.FromMilliseconds(60), new(FieldSubscriptionSamplingMode.HighestFrequencyShared));
            await owner.SecondBorrowQueryStarted.Task.WaitAsync(cancellationToken);
            await composition.StopAsync();
            var countAtStop = owner.BorrowQueryCount;
            await Task.Delay(TimeSpan.FromMilliseconds(intervalMilliseconds * 2), cancellationToken);
            Require(owner.BorrowQueryCount == countAtStop,
                "StopAsync retires active native demand and prevents later borrow queries");

            await composition.DisposeAsync();
            var direct = await owner.Backend.CollectAsync([QueryField], cancellationToken);
            Require(owner.DirectCollectionCount == 1 && direct.Length == 1
                && direct[0].Status == FieldObservationStatus.Current
                && owner.Backend.ReadCached(QueryField) == direct[0],
                "the caller-owned backend still collects and reads its single current after subscription stop/disposal");
            Require(owner.BorrowQueryCount == countAtStop,
                "the direct owner read is separate from the stopped subscription borrow");
        }
        finally
        {
            owner.ReleaseFirstBorrowQuery();
            fastDemand?.Dispose();
            await stream.DisposeAsync();
        }
    }

    private static async Task VerifyHighestFrequencyUsesLatestCurrentAsync(
        ConcurrentQueue<SubscriptionWorkerFailure> failures, CancellationToken cancellationToken)
    {
        using var owner = new RecordedSamplingOwner(pauseFirstBorrowQuery: false);
        var borrow = owner.BorrowSubscriptionCapture();
        Require(borrow.Bind([QueryField]).Single().SourceKind == FieldCollectionSourceKind.SourceQuery,
            "the highest-frequency field is bound to the original query source");

        await using var composition = CreateComposition(borrow, failures.Enqueue);
        await composition.StartAsync(cancellationToken);
        var settings = new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.HighestFrequencyShared);
        using var fastDemand = composition.Acquire("recorded-fast-mode", [QueryField],
            TimeSpan.FromMilliseconds(100), settings);
        await owner.FirstBorrowQueryCommitted.Task.WaitAsync(cancellationToken);

        owner.BeginCacheReadAudit();
        var stream = composition.SubscribeAsync("recorded-slow-cache-mode", [QueryField],
            TimeSpan.FromMilliseconds(675), settings, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(cancellationToken),
                "the slower HighestFrequencyShared subscription receives a completed current");
            var delivered = stream.Current.Single();
            var cacheRead = owner.CacheReadAudits.Single(static item => item.Field == QueryField);
            Require(delivered == cacheRead.Reading
                && cacheRead.CurrentRevision == cacheRead.LastCommittedBorrowQuery
                && cacheRead.BorrowQueryCountAtRead == cacheRead.LastCommittedBorrowQuery
                && cacheRead.LastCommittedBorrowQuery >= 3,
                "the slow subscription returns the latest committed owner cache after multiple faster source queries");
            Require(delivered.Status == FieldObservationStatus.Current
                && delivered.ObservedAt == cacheRead.Reading.ObservedAt,
                "the cache delivery preserves the original query observation timestamp");
        }
        finally
        {
            await stream.DisposeAsync();
        }

        await composition.StopAsync();
    }

    private static NativeFieldSubscriptionComposition CreateComposition(IReadWriteCollectionBorrow borrow,
        Action<SubscriptionWorkerFailure> reportFailure)
    {
        var settings = new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        return new(borrow, settings, in configuration, reportFailure, TimeSpan.FromMilliseconds(20));
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class RecordedSamplingOwner : IDisposable
    {
        private const long NoQueryValueOverride = long.MinValue;
        private readonly object gate = new();
        private readonly bool pauseFirstBorrowQuery;
        private readonly TaskCompletionSource<long> releaseFirstBorrowQuery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> firstBorrowQueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> firstBorrowQueryCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> secondBorrowQueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<long, FieldCollectionResult> borrowResults = new();
        private ImmutableDictionary<FieldReference, FieldReading> current;
        private CurrentState currentState;
        private ImmutableArray<CacheReadAudit> cacheReadAudits = [];
        private bool recordCacheReads;
        private long borrowQueryCount;
        private long directCollectionCount;
        private long generatedQueryValue = 5;
        private long nextQueryValueOverride = NoQueryValueOverride;

        internal RecordedSamplingOwner(bool pauseFirstBorrowQuery)
        {
            this.pauseFirstBorrowQuery = pauseFirstBorrowQuery;
            QueryField = SubscriptionSamplingModeConsumer.QueryField;
            PassiveField = SubscriptionSamplingModeConsumer.PassiveField;
            Catalog = new(
                [new(DeviceIdentity, "recorded", null)],
                [Capability(QueryField), Capability(PassiveField)]);
            var initialAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            current = ImmutableDictionary<FieldReference, FieldReading>.Empty
                .Add(QueryField, Reading(QueryField, 5, initialAt))
                .Add(PassiveField, Reading(PassiveField, 7, initialAt));
            currentState = new(0, 0);
            Backend = new RecordedBackend(this);
        }

        internal FieldReference QueryField { get; }
        internal FieldReference PassiveField { get; }
        internal ReadWriteCatalog Catalog { get; }
        internal IReadWriteBackend Backend { get; }
        internal SamplingPublicationSignal Publications { get; } = new();
        internal TaskCompletionSource<long> FirstBorrowQueryStarted => firstBorrowQueryStarted;
        internal TaskCompletionSource<long> FirstBorrowQueryCommitted => firstBorrowQueryCommitted;
        internal TaskCompletionSource<long> SecondBorrowQueryStarted => secondBorrowQueryStarted;
        internal long BorrowQueryCount => Interlocked.Read(ref borrowQueryCount);
        internal long DirectCollectionCount => Interlocked.Read(ref directCollectionCount);
        internal FieldCollectionResult GetBorrowResult(long querySequence)
            => borrowResults.TryGetValue(querySequence, out var result)
                ? result : throw new InvalidOperationException($"No query result was recorded for sequence {querySequence}.");

        internal IReadWriteCollectionBorrow BorrowSubscriptionCapture() => new RecordedBorrow(this);

        internal void SetNextQueryValue(long value) => Interlocked.Exchange(ref nextQueryValueOverride, value);
        internal void ReleaseFirstBorrowQuery() => releaseFirstBorrowQuery.TrySetResult(1);

        internal void BeginCacheReadAudit()
        {
            lock (gate)
            {
                cacheReadAudits = [];
                recordCacheReads = true;
            }
        }

        internal ImmutableArray<CacheReadAudit> CacheReadAudits
        {
            get { lock (gate) return cacheReadAudits; }
        }

        public void Dispose() => releaseFirstBorrowQuery.TrySetResult(0);

        private async ValueTask<FieldCollectionResult> CollectBorrowedAsync(
            ImmutableArray<FieldReference> fields, IMetricPublicationPermit permit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queriesSource = fields.Contains(QueryField);
            var querySequence = queriesSource ? Interlocked.Increment(ref borrowQueryCount) : 0;
            if (querySequence == 1)
            {
                firstBorrowQueryStarted.TrySetResult(querySequence);
                if (pauseFirstBorrowQuery)
                    await releaseFirstBorrowQuery.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (querySequence == 2)
            {
                secondBorrowQueryStarted.TrySetResult(querySequence);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ImmutableDictionary<FieldReference, FieldReading> before;
            lock (gate) before = current;
            var queryReading = queriesSource
                ? Reading(QueryField, NextQueryValue(), DateTimeOffset.UtcNow)
                : null;
            var readings = fields.Select(field => field == QueryField && queryReading is not null
                ? queryReading : before[field]).ToImmutableArray();
            var queriedFields = queriesSource ? ImmutableArray.Create(QueryField) : ImmutableArray<FieldReference>.Empty;
            var committed = permit.TryPublish(() =>
            {
                lock (gate)
                {
                    if (queryReading is not null) current = current.SetItem(QueryField, queryReading);
                    currentState = new(queryReading is null ? currentState.Revision : checked(currentState.Revision + 1),
                        queryReading is null ? currentState.LastBorrowQuery : querySequence);
                }
                Publications.Publish([CollectionItemId]);
            });
            var result = new FieldCollectionResult(readings, committed, queriedFields);
            if (querySequence > 0) borrowResults[querySequence] = result;
            if (committed && queryReading is not null && querySequence == 1)
                firstBorrowQueryCommitted.TrySetResult(querySequence);
            return result;
        }

        private ValueTask<ImmutableArray<FieldReading>> CollectDirectAsync(
            ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queryReading = fields.Contains(QueryField)
                ? Reading(QueryField, NextQueryValue(), DateTimeOffset.UtcNow) : null;
            ImmutableDictionary<FieldReference, FieldReading> snapshot;
            lock (gate)
            {
                if (queryReading is not null)
                {
                    current = current.SetItem(QueryField, queryReading);
                    currentState = new(checked(currentState.Revision + 1), currentState.LastBorrowQuery);
                    Interlocked.Increment(ref directCollectionCount);
                }
                snapshot = current;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Publications.Publish([CollectionItemId]);
            return ValueTask.FromResult<ImmutableArray<FieldReading>>([.. fields.Select(field => snapshot[field])]);
        }

        private long NextQueryValue()
        {
            var requested = Interlocked.Exchange(ref nextQueryValueOverride, NoQueryValueOverride);
            if (requested == NoQueryValueOverride) return Interlocked.Increment(ref generatedQueryValue);
            Interlocked.Exchange(ref generatedQueryValue, requested);
            return requested;
        }

        private FieldReading ReadCached(FieldReference field)
        {
            lock (gate)
            {
                var reading = current[field];
                if (recordCacheReads)
                    cacheReadAudits = cacheReadAudits.Add(new(field, reading,
                        currentState.Revision, currentState.LastBorrowQuery, BorrowQueryCount));
                return reading;
            }
        }

        private static FieldCapability Capability(FieldReference field) => new(field,
            new(field.FieldName, "count", "float64"),
            new(true, null, []), OperationSupport.Unsupported("read-only"), SourceId);

        private static FieldReading Reading(FieldReference field, double value, DateTimeOffset observedAt)
            => new(field, FieldObservationStatus.Current, new(Number: value), observedAt, SourceId);

        private sealed record CurrentState(long Revision, long LastBorrowQuery);
        internal sealed record CacheReadAudit(FieldReference Field, FieldReading Reading,
            long CurrentRevision, long LastCommittedBorrowQuery, long BorrowQueryCountAtRead);

        private sealed class RecordedBorrow(RecordedSamplingOwner owner) : IReadWriteCollectionBorrow
        {
            private int disposed;
            public IReadWriteBackend Backend => owner.Backend;
            public SamplingPublicationSignal Publications => owner.Publications;

            public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                foreach (var field in fields) _ = owner.Catalog.Require(field);
                return [.. fields.Select(field => new FieldCollectionBinding(field,
                    [CollectionItemId], [CollectionItemId], field == owner.QueryField
                        ? FieldCollectionSourceKind.SourceQuery : FieldCollectionSourceKind.CurrentValue))];
            }

            public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                foreach (var field in activeFields) _ = owner.Catalog.Require(field);
            }

            public ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
                IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                return owner.CollectBorrowedAsync(fields, permit, cancellationToken);
            }

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        }

        private sealed class RecordedBackend(RecordedSamplingOwner owner) : IReadWriteBackend
        {
            public ReadWriteCatalog Catalog => owner.Catalog;
            public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Catalog);
            }
            public FieldReading ReadCached(FieldReference field)
            {
                _ = Catalog.Require(field);
                return owner.ReadCached(field);
            }
            public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields,
                CancellationToken cancellationToken) => owner.CollectDirectAsync(fields, cancellationToken);
            public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = Catalog.Require(field);
                return ValueTask.FromResult(new FieldWriteResult(field, "unsupported", "recorded-read-only"));
            }
        }
    }
}
