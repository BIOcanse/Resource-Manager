using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Results.Explicit;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class WindowsExplicitResultSubscriptionConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var owner = new WindowsExplicitResultOwner("recorded-host-device");
        var backend = owner.CreateBackend();
        var resultDeviceIdentity = backend.Catalog.Devices.Single(static device => device.Resource == "test_result").Identity;
        var score = new FieldReference(resultDeviceIdentity, "cpu_all_benchmark_score");
        var duration = new FieldReference(resultDeviceIdentity, "cpu_all_benchmark_duration_ms");
        owner.Submit(Benchmark(5d, 100d, "initial recorded result"));

        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        var recordingBorrow = new RecordingBorrow(owner.BorrowSubscriptionCapture());
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(recordingBorrow,
            new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000), in configuration,
            failures.Enqueue);
        await composition.StartAsync(timeout.Token);
        using var fastDemand = composition.Acquire("recorded-fast-result-demand", [score], TimeSpan.FromMilliseconds(100));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var stream = composition.SubscribeAsync("recorded-explicit-result", [score, duration],
            TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = stream.MoveNextAsync().AsTask();
        await Task.Yield();
        var committed = owner.Submit(Benchmark(0d, 125d, "replacement recorded result"));
        Require(await pending.WaitAsync(timeout.Token), "submission publication wakes the explicit role subscription");
        Require(stream.Current.Length == 2
            && backend.ReadCached(score) == stream.Current.Single(item => item.Field == score)
            && backend.ReadCached(duration) == stream.Current.Single(item => item.Field == duration),
            "subscription reads the same one immutable submitted current through Information");
        Require(committed.Values.CpuAllBenchmarkScore?.Value == 0d
            && stream.Current.Single(item => item.Field == score).Value?.Number == 0d,
            "valid zero survives the public subscription projection");

        var fastBaseline = recordingBorrow.CollectionCount;
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        var fastCollections = recordingBorrow.CollectionCount - fastBaseline;
        Require(fastCollections >= 2,
            "an explicitly acquired 100 ms demand accelerates the same borrowed result collection");

        var slowBaseline = recordingBorrow.CollectionCount;
        fastDemand.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(1250), timeout.Token);
        var slowCollections = recordingBorrow.CollectionCount - slowBaseline;
        Require(slowCollections is >= 1 and <= 3,
            "releasing the faster demand returns the shared result collection to the 1 s observer cadence");

        var beforeFailure = owner.ReadCurrent();
        try
        {
            var malformed = Benchmark(10d, 100d, "bad unit");
            owner.Submit(malformed with
            { Values = malformed.Values with
                { CpuAllBenchmarkScore = new(10d, "fps") } });
            throw new InvalidOperationException("The explicit result owner accepted a malformed submission.");
        }
        catch (ArgumentException) { }
        Require(ReferenceEquals(beforeFailure, owner.ReadCurrent()),
            "a failed explicit submission leaves the subscriber's original current intact");

        cancellation.Cancel();
        // A periodic observer can already have a completed round buffered.
        // Cancel first to check that even that round cannot be delivered afterward.
        var next = stream.MoveNextAsync().AsTask();
        try
        {
            _ = await next.WaitAsync(timeout.Token);
            throw new InvalidOperationException("The canceled result observer returned another value.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await stream.DisposeAsync();
        await composition.StopAsync();
        var afterLastRelease = recordingBorrow.CollectionCount;
        await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        Require(recordingBorrow.CollectionCount == afterLastRelease,
            "cancellation and last-demand release stop further collection");
        Require(backend.ReadCached(score).Value?.Number == 0d && failures.IsEmpty,
            "stop/cancellation releases planner demand while the owner current remains readable");
        Console.WriteLine("SubscriptionCore explicit-result consumer passed: explicit Start/Acquire/Subscribe/Stop over the original borrow, same current, zero, validation failure, and cancellation.");
    }

    private static WindowsExplicitResultSubmission Benchmark(double score, double duration, string note)
    {
        using var metadata = JsonDocument.Parse(JsonSerializer.SerializeToElement(new { fixture = note }).GetRawText());
        var resultMetadata = new WindowsExplicitResultMetadata("recorded-host-device", WindowsExplicitResultScope.Cpu,
            "cpu:recorded-0", "recorded-session", "recorded-suite", "1",
            new DateTimeOffset(2026, 10, 7, 18, 30, 0, TimeSpan.Zero),
            "recorded-score-method", "workload=recorded", metadata.RootElement.Clone());
        return new(1, WindowsExplicitResultKind.CpuBenchmark, resultMetadata,
            new()
            {
                CpuAllBenchmarkDurationMs = new(duration, "ms"),
                CpuAllBenchmarkScore = new(score, "score")
            });
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
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class RecordingBorrow(IReadWriteCollectionBorrow inner) : IReadWriteCollectionBorrow
    {
        private long collectionCount;
        internal long CollectionCount => Interlocked.Read(ref collectionCount);
        public IReadWriteBackend Backend => inner.Backend;
        public ResourceManager.ReadWrite.Publications.SamplingPublicationSignal Publications => inner.Publications;
        public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields) => inner.Bind(fields);
        public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields) => inner.SynchronizeDemand(activeFields);
        public async ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
            ResourceManager.App.Infrastructure.Monitoring.IMetricPublicationPermit permit,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref collectionCount);
            return await inner.CollectAsync(fields, permit, cancellationToken).ConfigureAwait(false);
        }
        public void Dispose() => inner.Dispose();
    }
}
