using ResourceManager.App.Domain.Metrics;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class FrameFieldSubscriptionConsumer
{
    private const int ProcessId = 51;
    private const long ProcessBirthFileTimeUtc = 134056632000000000;
    private const ulong SwapChain = 0xA0;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(750);
    private static readonly PresentFrameFieldQuery Query = new(ProcessId, ProcessBirthFileTimeUtc,
        FramePresentSource.Dxgi, SwapChain, Window, FrameTimePercentile.P95);
    private static readonly ImmutableArray<FieldReference> Fields =
    [
        Query.Field(FrameFieldRegistry.PresentedFps),
        Query.Field(FrameFieldRegistry.PresentTimeActual),
        Query.Field(FrameFieldRegistry.DisplayIntervalMs),
        Query.Field(FrameFieldRegistry.DisplayedFps),
        Query.Field(FrameFieldRegistry.DroppedCount),
        Query.Field(FrameFieldRegistry.DisplayIntervalP95Ms),
        Query.Field(FrameFieldRegistry.DisplayIntervalP99Ms)
    ];

    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await VerifyRateMergeCurrentAndLastReleaseAsync(timeout.Token);
        await VerifySubscriptionCancellationReleasesDemandAsync(timeout.Token);
        await VerifySubscriptionFailurePublishesNullAndStopsAsync(timeout.Token);
        Console.WriteLine("SubscriptionCore frame consumer passed: recorded display and Present current, merged cadence, last-demand release, cancellation, failure and stop.");
    }

    private static async Task VerifyRateMergeCurrentAndLastReleaseAsync(CancellationToken cancellationToken)
    {
        var source = new RecordedPresentSource();
        using var owner = new WindowsPresentFrameFieldOwner(source, Query);
        var backend = owner.CreateBackend();
        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        await using var composition = CreateComposition(owner.BorrowSubscriptionCapture(), failures.Enqueue);
        await composition.StartAsync(cancellationToken);

        using var fastDemand = composition.Acquire("recorded-frame-fast", Fields, TimeSpan.FromMilliseconds(100));
        var observer = composition.SubscribeAsync("recorded-frame-slow", Fields,
            TimeSpan.FromMilliseconds(650), cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            await WaitForReadCountAsync(source, 4, TimeSpan.FromSeconds(4), cancellationToken);
            Require(await observer.MoveNextAsync(), "frame subscription published an observation");
            RequireSameCurrent(backend, observer.Current, "subscription uses the owner's single current");
            Require(source.Acquisitions == 1 && source.ActiveLeases == 1,
                "multiple demands borrow one original source lease");
            Require(backend.ReadCached(Query.Field(FrameFieldRegistry.DisplayIntervalMs)).Value?.Number is > 0,
                "the package consumer reads a measured display interval from the owner's current");
            Require(backend.ReadCached(Query.Field(FrameFieldRegistry.DroppedCount)).Value?.Integer == MetricInteger.Parse("0"),
                "a complete recorded display window preserves valid zero drops");

            var fastBaseline = source.ReadCount;
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
            var fastSamples = source.ReadCount - fastBaseline;
            Require(fastSamples >= 3, "100 ms demand accelerates the shared 650 ms subscription item");

            var slowBaseline = source.ReadCount;
            fastDemand.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
            var slowSamples = source.ReadCount - slowBaseline;
            Require(slowSamples is >= 1 and <= 5,
                "after fastest-demand release the 650 ms subscription remains and the faster cadence ends");
        }
        finally
        {
            await observer.DisposeAsync();
        }

        await WaitForLeaseCountAsync(source, 0, TimeSpan.FromSeconds(4), cancellationToken);
        var countAfterLastRelease = source.ReadCount;
        await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        Require(source.ReadCount == countAfterLastRelease, "last frame demand releases the original source lease and stops collection");
        await composition.StopAsync();
        Require(failures.IsEmpty, "recorded source completed without worker failure");
    }

    private static async Task VerifySubscriptionCancellationReleasesDemandAsync(CancellationToken cancellationToken)
    {
        var source = new RecordedPresentSource();
        using var owner = new WindowsPresentFrameFieldOwner(source, Query);
        var backend = owner.CreateBackend();
        await using var composition = CreateComposition(owner.BorrowSubscriptionCapture(), static _ => { });
        await composition.StartAsync(cancellationToken);
        using var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observer = composition.SubscribeAsync("recorded-frame-cancel", Fields,
            TimeSpan.FromMilliseconds(150), subscriptionCancellation.Token)
            .GetAsyncEnumerator(subscriptionCancellation.Token);
        try
        {
            Require(await observer.MoveNextAsync(), "frame subscription publishes before caller cancellation");
            RequireSameCurrent(backend, observer.Current, "pre-cancellation frame readings use the owner current");
            subscriptionCancellation.Cancel();
            try
            {
                _ = await observer.MoveNextAsync();
                throw new InvalidOperationException("Canceled frame subscription returned another reading.");
            }
            catch (OperationCanceledException) when (subscriptionCancellation.IsCancellationRequested) { }
        }
        finally
        {
            await observer.DisposeAsync();
        }

        await WaitForLeaseCountAsync(source, 0, TimeSpan.FromSeconds(4), cancellationToken);
        await composition.StopAsync();
        Require(source.ActiveLeases == 0, "caller cancellation releases the borrowed source lease");
    }

    private static async Task VerifySubscriptionFailurePublishesNullAndStopsAsync(CancellationToken cancellationToken)
    {
        var source = new RecordedPresentSource { FailAcquisitions = true };
        using var owner = new WindowsPresentFrameFieldOwner(source, Query);
        var backend = owner.CreateBackend();
        var failure = new TaskCompletionSource<SubscriptionWorkerFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var composition = CreateComposition(owner.BorrowSubscriptionCapture(), item => failure.TrySetResult(item));
        await composition.StartAsync(cancellationToken);
        var observer = composition.SubscribeAsync("recorded-frame-failure", Fields,
            TimeSpan.FromMilliseconds(250), cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            Require(await observer.MoveNextAsync(), "failed source-lease acquisition publishes the selected unavailable current");
            RequireSameCurrent(backend, observer.Current, "failed subscription still reads the owner's current");
            var timestamp = observer.Current.Single(item => item.Field.FieldName == FrameFieldRegistry.PresentTimeActual);
            Require(timestamp.Status == FieldObservationStatus.Unavailable && timestamp.Value is null,
                "failed Present source-lease acquisition publishes null, not a numeric placeholder");
            var dropped = observer.Current.Single(item => item.Field.FieldName == FrameFieldRegistry.DroppedCount);
            Require(dropped.Status == FieldObservationStatus.Unavailable && dropped.Value is null,
                "failed Present source-lease acquisition does not turn absent display evidence into zero drops");
            var reported = await failure.Task.WaitAsync(cancellationToken);
            Require(reported.Stage == SubscriptionWorkerFailureStage.Collection,
                "recorded source failure is reported by the original worker collection path");

            await composition.StopAsync();
            Require(!await observer.MoveNextAsync(), "stop retires the failed frame subscription");
        }
        finally
        {
            await observer.DisposeAsync();
        }

        Require(source.ActiveLeases == 0, "failed and stopped subscription releases the original source lease");
    }

    private static NativeFieldSubscriptionComposition CreateComposition(
        IReadWriteCollectionBorrow borrow, Action<SubscriptionWorkerFailure> reportFailure)
    {
        // Match the package's established role-1 contract used by information owners.
        var settings = new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
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
        return new(borrow, settings, in configuration, reportFailure, TimeSpan.FromMilliseconds(250));
    }

    private static async Task WaitForReadCountAsync(RecordedPresentSource source, int minimum,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        while (source.ReadCount < minimum) await Task.Delay(TimeSpan.FromMilliseconds(10), wait.Token);
    }

    private static async Task WaitForLeaseCountAsync(RecordedPresentSource source, int expected,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        while (source.ActiveLeases != expected) await Task.Delay(TimeSpan.FromMilliseconds(10), wait.Token);
    }

    private static void RequireSameCurrent(IReadWriteBackend backend,
        ImmutableArray<FieldReading> readings, string message)
    {
        Require(readings.Length == Fields.Length, message);
        foreach (var reading in readings)
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, backend.ReadCached(reading.Field));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class RecordedPresentSource : IFrameTimingObservationSource
    {
        private int reads;
        private int acquisitions;
        private int activeLeases;

        internal int ReadCount => Volatile.Read(ref reads);
        internal int Acquisitions => Volatile.Read(ref acquisitions);
        internal int ActiveLeases => Volatile.Read(ref activeLeases);
        internal bool FailAcquisitions { get; init; }

        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref acquisitions);
            if (FailAcquisitions) throw new IOException("recorded Present source lease failure");
            Interlocked.Increment(ref activeLeases);
            return new SourceLease(() => Interlocked.Decrement(ref activeLeases));
        }

        public FrameTimingSnapshot? Read(TimeSpan window)
        {
            var read = Interlocked.Increment(ref reads);
            var observedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(read);
            var statistics = FrameIntervalStatistics.Compute([10d, 20d, 30d])!;
            return new FrameTimingSnapshot(observedAt, window, true,
            [new(ProcessId, ProcessBirthFileTimeUtc,
                [new(FramePresentSource.Dxgi, SwapChain, observedAt, statistics)])])
            {
                LastPresents =
                [new(ProcessId, ProcessBirthFileTimeUtc, FramePresentSource.Dxgi, SwapChain, observedAt)],
                DisplayStreams =
                [new(ProcessId, ProcessBirthFileTimeUtc, FramePresentSource.Dxgi, SwapChain,
                    true, statistics, 0UL, [])]
            };
        }

        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, []);

        private sealed class SourceLease(Action release) : IDisposable
        {
            private Action? holder = release;
            public void Dispose() => Interlocked.Exchange(ref holder, null)?.Invoke();
        }
    }
}
