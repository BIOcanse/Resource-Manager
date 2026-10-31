using System.Collections;
using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Frames;

namespace Resource_Manager_APP.Tests;

public sealed class FrameFieldOwnerLibraryIntegrationTests
{
    private const int ProcessId = 41;
    private const long ProcessBirth = 134056632000000000;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(750);
    private static readonly DateTimeOffset Through = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly double[] Intervals = Enumerable.Range(1, 100).Select(static item => (double)item).ToArray();

    private static PresentFrameFieldQuery Query(FrameTimePercentile percentile = FrameTimePercentile.P95)
        => new(ProcessId, ProcessBirth, FramePresentSource.Dxgi, 0xA0, Window, percentile);

    [Fact]
    public void RegistryHasTheExactThirteenNamesAndKeepsMissingDisplayEvidenceUnavailable()
    {
        var source = new FakePresentSource();
        using var owner = new WindowsPresentFrameFieldOwner(source, Query());
        var backend = owner.CreateBackend();
        var expected = new[]
        {
            "frame_all_display_interval_ms", "frame_all_displayed_fps", "frame_all_dropped_count",
            "frame_all_fps_derived", "frame_all_present_interval_ms", "frame_all_present_time_actual",
            "frame_all_presented_fps", "frame_all_display_interval_p95_ms", "frame_all_display_interval_p99_ms",
            "frame_all_present_interval_p95_ms", "frame_all_present_interval_p99_ms",
            "frame_all_time_percentile_derived", "frame_all_window_duration_configured"
        };

        Assert.Equal(expected.Order(StringComparer.Ordinal), FrameFieldRegistry.Definitions
            .Select(static item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(13, backend.Catalog.Fields.Length);
        foreach (var name in new[]
        {
            FrameFieldRegistry.DisplayIntervalMs, FrameFieldRegistry.DisplayedFps,
            FrameFieldRegistry.DroppedCount, FrameFieldRegistry.DisplayIntervalP95Ms,
            FrameFieldRegistry.DisplayIntervalP99Ms
        })
        {
            var capability = Assert.Single(backend.Catalog.Fields.Where(item => item.Field.FieldName == name));
            Assert.True(capability.Read.Supported);
            Assert.Null(capability.Read.Reason);
            Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(capability.Field).Status);
        }

        Assert.Equal(FrameFieldRegistry.FrameAllPresentedFps.Quantity, FrameFieldRegistry.FrameAllFpsDerived.Quantity);
        Assert.Equal("ms", FrameFieldRegistry.FrameAllPresentIntervalP95Ms.Unit);
        Assert.Equal("fps", FrameFieldRegistry.FrameAllPresentedFps.Unit);
        Assert.Equal("s", FrameFieldRegistry.FrameAllWindowDurationConfigured.Unit);
        Assert.Equal(0, source.Acquisitions);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public async Task CachedReadIsPassiveAndCaptureUsesExactProcessStreamWindowAndUnixZero()
    {
        var statistics = FrameIntervalStatistics.Compute(Intervals)!;
        var expected = new FrameTimingSnapshot(Through, Window, true,
        [
            new(ProcessId, ProcessBirth,
            [
                new(FramePresentSource.Dxgi, 0xA0, DateTimeOffset.UnixEpoch, statistics),
                new(FramePresentSource.Dxgi, 0xB0, Through, FrameIntervalStatistics.Compute([90, 90])!),
                new(FramePresentSource.Direct3D9, 0xA0, Through, FrameIntervalStatistics.Compute([1, 1])!)
            ]),
            new(ProcessId, ProcessBirth + 1,
            [new(FramePresentSource.Dxgi, 0xA0, Through, FrameIntervalStatistics.Compute([80, 80])!)]),
            new(ProcessId + 1, ProcessBirth,
            [new(FramePresentSource.Dxgi, 0xA0, Through, FrameIntervalStatistics.Compute([70, 70])!)])
        ]);
        var source = new FakePresentSource { ReadHandler = _ => expected };
        using var owner = new WindowsPresentFrameFieldOwner(source, Query());
        var backend = owner.CreateBackend();
        var timestampField = Query().Field(FrameFieldRegistry.PresentTimeActual);

        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(timestampField).Status);
        Assert.Equal(0, source.Acquisitions);
        Assert.Equal(0, source.ReadCount);

        var readings = await backend.CollectAsync(Query().Fields, CancellationToken.None);

        Assert.Equal(Window, source.LastWindow);
        Assert.Equal(1, source.Acquisitions);
        Assert.Equal(1, source.ActiveLeases);
        Assert.Equal(0d, Assert.Single(readings.Where(item => item.Field.FieldName == FrameFieldRegistry.PresentTimeActual)).Value!.Number!.Value);
        Assert.Equal(statistics.DurationMs / statistics.IntervalCount,
            Number(backend, FrameFieldRegistry.PresentIntervalMs), 9);
        Assert.Equal(statistics.AverageFps, Number(backend, FrameFieldRegistry.FpsDerived), 9);
        Assert.Equal(Number(backend, FrameFieldRegistry.FpsDerived), Number(backend, FrameFieldRegistry.PresentedFps));
        Assert.Equal(statistics.P95FrameTimeMs, Number(backend, FrameFieldRegistry.PresentIntervalP95Ms));
        Assert.Equal(statistics.P99FrameTimeMs, Number(backend, FrameFieldRegistry.PresentIntervalP99Ms));
        Assert.Equal(statistics.P95FrameTimeMs, Number(backend, FrameFieldRegistry.TimePercentileDerived));
        Assert.Equal(Window.TotalSeconds, Number(backend, FrameFieldRegistry.WindowDurationConfigured));
        Assert.Equal<DateTimeOffset?>(Through, backend.ReadCached(timestampField).ObservedAt);
    }

    [Fact]
    public async Task MixedFrameBorrowMarksConfiguredMetadataPassiveAndOnlyQueriesSelectedMeasurements()
    {
        var query = Query();
        var source = SourceWithTargetStream(query, Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, query);
        var configured = query.Field(FrameFieldRegistry.WindowDurationConfigured);
        var presentedFps = query.Field(FrameFieldRegistry.PresentedFps);
        using var borrow = owner.BorrowSubscriptionCapture();

        var bindings = borrow.Bind([configured, presentedFps]).ToDictionary(static binding => binding.Field);
        Assert.Equal(FieldCollectionSourceKind.CurrentValue, bindings[configured].SourceKind);
        Assert.Equal(FieldCollectionSourceKind.SourceQuery, bindings[presentedFps].SourceKind);
        Assert.Equal(0, source.ReadCount);

        var result = await borrow.CollectAsync([configured, presentedFps], new AcceptingPermit());
        Assert.True(result.Committed);
        Assert.Equal(new[] { presentedFps }, result.QueriedFields.ToArray());
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(Window.TotalSeconds, Number(owner.CreateBackend(), FrameFieldRegistry.WindowDurationConfigured));
        Assert.Equal(FieldObservationStatus.Current, owner.CreateBackend().ReadCached(presentedFps).Status);
    }

    [Fact]
    public async Task SinglePresentProvidesTimestampButNoIntervalOrFps()
    {
        var query = Query();
        var presentAt = Through.AddMilliseconds(-10);
        var source = new FakePresentSource
        {
            ReadHandler = _ => new FrameTimingSnapshot(Through, Window, true, [])
            {
                LastPresents =
                [new(query.ProcessId, query.ProcessStartFileTimeUtc, query.Source, query.SwapChain, presentAt)]
            }
        };
        using var owner = new WindowsPresentFrameFieldOwner(source, query);
        var backend = owner.CreateBackend();
        var timestamp = query.Field(FrameFieldRegistry.PresentTimeActual);
        var interval = query.Field(FrameFieldRegistry.PresentIntervalMs);
        var fps = query.Field(FrameFieldRegistry.PresentedFps);

        await backend.CollectAsync([timestamp, interval, fps], CancellationToken.None);

        Assert.Equal((double)presentAt.ToUnixTimeMilliseconds(), backend.ReadCached(timestamp).Value!.Number);
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(interval).Status);
        Assert.Null(backend.ReadCached(interval).Value);
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(fps).Status);
        Assert.Null(backend.ReadCached(fps).Value);
    }

    [Theory]
    [InlineData(FrameTimePercentile.P50, 50d)]
    [InlineData(FrameTimePercentile.P95, 95d)]
    [InlineData(FrameTimePercentile.P99, 99d)]
    public async Task DerivedPercentileUsesTheExplicitNearestRankParameter(
        FrameTimePercentile percentile, double expected)
    {
        var query = Query(percentile);
        var source = SourceWithTargetStream(query, Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, query);
        var backend = owner.CreateBackend();

        await backend.CollectAsync([query.Field(FrameFieldRegistry.TimePercentileDerived)], CancellationToken.None);

        Assert.Equal(expected, Number(backend, query, FrameFieldRegistry.TimePercentileDerived));
    }

    [Fact]
    public async Task IncompleteAndNoSampleObservationsClearOnlySelectedValuesAndNeverInventZeroFps()
    {
        var source = SourceWithTargetStream(Query(), Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, Query());
        var backend = owner.CreateBackend();
        var percentile = Query().Field(FrameFieldRegistry.PresentIntervalP99Ms);
        var presentedFps = Query().Field(FrameFieldRegistry.PresentedFps);
        var unselectedFps = Query().Field(FrameFieldRegistry.FpsDerived);
        await backend.CollectAsync([percentile, presentedFps, unselectedFps], CancellationToken.None);
        var originalP99 = backend.ReadCached(percentile);
        var originalFps = backend.ReadCached(unselectedFps);

        source.ReadHandler = _ => new(Through.AddMilliseconds(500), Window, false, []);
        await backend.CollectAsync([presentedFps], CancellationToken.None);

        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(presentedFps).Status);
        Assert.Null(backend.ReadCached(presentedFps).Value);
        Assert.Equal(originalP99, backend.ReadCached(percentile));
        Assert.Equal(originalFps, backend.ReadCached(unselectedFps));
        Assert.Equal(Window.TotalSeconds, Number(backend, FrameFieldRegistry.WindowDurationConfigured));

        source.ReadHandler = _ => new(Through.AddSeconds(1), Window, true, []);
        await backend.CollectAsync([presentedFps], CancellationToken.None);
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(presentedFps).Status);
        Assert.Null(backend.ReadCached(presentedFps).Value);
    }

    [Fact]
    public async Task ReadFailureClearsSelectedObservationAndKeepsConfiguredWindow()
    {
        var source = SourceWithTargetStream(Query(), Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, Query());
        var backend = owner.CreateBackend();
        var fps = Query().Field(FrameFieldRegistry.PresentedFps);
        await backend.CollectAsync([fps], CancellationToken.None);
        source.ReadHandler = _ => throw new IOException("recorded source failure");

        await Assert.ThrowsAsync<IOException>(async () =>
            await backend.CollectAsync([fps], CancellationToken.None));

        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(fps).Status);
        Assert.Null(backend.ReadCached(fps).Value);
        Assert.Equal(Window.TotalSeconds, Number(backend, FrameFieldRegistry.WindowDurationConfigured));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceLeaseAcquisitionFailureClearsOnlySelectedFieldAndRethrows(bool borrowed)
    {
        var query = Query();
        var source = SourceWithTargetStream(query, Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, query);
        var backend = owner.CreateBackend();
        var fps = query.Field(FrameFieldRegistry.PresentedFps);
        var timestamp = query.Field(FrameFieldRegistry.PresentTimeActual);

        if (borrowed)
        {
            await backend.CollectAsync([fps, timestamp], CancellationToken.None);
        }
        else
        {
            using var seed = owner.BorrowSubscriptionCapture();
            var result = await seed.CollectAsync([fps, timestamp], new AcceptingPermit());
            Assert.True(result.Committed);
        }

        var previousTimestamp = backend.ReadCached(timestamp);
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(fps).Status);
        source.AcquireHandler = () => throw new IOException("recorded source lease failure");

        if (borrowed)
        {
            using var borrow = owner.BorrowSubscriptionCapture();
            await Assert.ThrowsAsync<IOException>(async () =>
                await borrow.CollectAsync([fps], new AcceptingPermit(), CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await backend.CollectAsync([fps], CancellationToken.None));
        }

        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(fps).Status);
        Assert.Null(backend.ReadCached(fps).Value);
        Assert.Equal(previousTimestamp, backend.ReadCached(timestamp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringProjectionDoesNotReplaceCurrent(bool borrowed)
    {
        var query = Query();
        var source = SourceWithTargetStream(query, Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, query);
        var backend = owner.CreateBackend();
        var fps = query.Field(FrameFieldRegistry.PresentedFps);
        await backend.CollectAsync([fps], CancellationToken.None);
        var previous = backend.ReadCached(fps);
        using var cancellation = new CancellationTokenSource();
        var replacement = SnapshotWithIntervals(query, Through.AddSeconds(1), Intervals);
        source.ReadHandler = _ => replacement with
        {
            Processes = new CancelOnEnumerationProcessList(replacement.Processes, cancellation.Cancel)
        };

        if (borrowed)
        {
            using var borrow = owner.BorrowSubscriptionCapture();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await borrow.CollectAsync([fps], new AcceptingPermit(), cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await backend.CollectAsync([fps], cancellation.Token));
        }

        Assert.Equal(previous, backend.ReadCached(fps));
    }

    [Fact]
    public async Task CancellationDoesNotPublishOrEraseThePreviousCurrent()
    {
        var source = SourceWithTargetStream(Query(), Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, Query());
        var backend = owner.CreateBackend();
        var fps = Query().Field(FrameFieldRegistry.PresentedFps);
        await backend.CollectAsync([fps], CancellationToken.None);
        var before = backend.ReadCached(fps);
        using var cancellation = new CancellationTokenSource();
        source.ReadHandler = _ =>
        {
            cancellation.Cancel();
            return new(Through.AddSeconds(1), Window, false, []);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await backend.CollectAsync([fps], cancellation.Token));

        Assert.Equal(before, backend.ReadCached(fps));
    }

    [Fact]
    public async Task DirectAndBorrowedCollectUseOneCurrentAndReleaseOnlyTheirOwnLeases()
    {
        var source = SourceWithTargetStream(Query(), Through);
        using var owner = new WindowsPresentFrameFieldOwner(source, Query());
        var backend = owner.CreateBackend();
        var fps = Query().Field(FrameFieldRegistry.PresentedFps);
        await backend.CollectAsync([fps], CancellationToken.None);
        var directCurrent = backend.ReadCached(fps);
        var borrow = owner.BorrowSubscriptionCapture();
        Assert.Same(backend, borrow.Backend);
        Assert.Same(owner.Publications, borrow.Publications);
        Assert.Equal(WindowsPresentFrameFieldOwner.CollectionItemId,
            Assert.Single(borrow.Bind([fps])).CollectionItemIds.Single());
        borrow.SynchronizeDemand([fps]);
        Assert.Equal(1, source.ActiveLeases);

        source.ReadHandler = _ => SnapshotWithIntervals(Query(), Through, Enumerable.Repeat(20d, 100).ToArray());
        var rejected = new RejectingPermit();
        var result = await borrow.CollectAsync([fps], rejected);

        Assert.False(result.Committed);
        Assert.Equal(2, source.ActiveLeases);
        Assert.Equal(directCurrent, backend.ReadCached(fps));
        Assert.Equal(1, rejected.Attempts);
        borrow.Dispose();
        Assert.Equal(1, source.ActiveLeases);
        owner.Dispose();
        Assert.Equal(0, source.ActiveLeases);
    }

    [Fact]
    public async Task RetirementDuringReadRejectsTheLateCommitAndDrainsItsLease()
    {
        var entered = new ManualResetEventSlim();
        var continueRead = new ManualResetEventSlim();
        var source = new FakePresentSource
        {
            ReadHandler = _ =>
            {
                entered.Set();
                if (!continueRead.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return SnapshotWithIntervals(Query(), Through, Intervals);
            }
        };
        var owner = new WindowsPresentFrameFieldOwner(source, Query());
        var backend = owner.CreateBackend();
        var fps = Query().Field(FrameFieldRegistry.PresentedFps);
        try
        {
            var collection = Task.Run(async () => await backend.CollectAsync([fps], CancellationToken.None));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            owner.Dispose();
            continueRead.Set();
            var result = await collection;

            Assert.Equal(FieldObservationStatus.Unavailable, Assert.Single(result).Status);
            Assert.Equal(0, source.ActiveLeases);
        }
        finally
        {
            continueRead.Set();
            owner.Dispose();
            entered.Dispose();
            continueRead.Dispose();
        }
    }

    [Fact]
    public void QueryRequiresTheRealProcessBirthAndOriginalWindowBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentFrameFieldQuery(
            0, ProcessBirth, FramePresentSource.Dxgi, 0xA0, Window, FrameTimePercentile.P95));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentFrameFieldQuery(
            ProcessId, 0, FramePresentSource.Dxgi, 0xA0, Window, FrameTimePercentile.P95));
        // The window budget is the source's declared retention, checked when an owner binds the query.
        var source = new FakePresentSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsPresentFrameFieldOwner(source, new PresentFrameFieldQuery(
            ProcessId, ProcessBirth, FramePresentSource.Dxgi, 0xA0,
            source.Retention + TimeSpan.FromTicks(1), FrameTimePercentile.P95)));
    }

    [Fact]
    public void QueryResultIdentitiesIncludeWindowAndPercentileForEveryWindowDependentField()
    {
        var first = Query(FrameTimePercentile.P95);
        var differentWindow = new PresentFrameFieldQuery(ProcessId, ProcessBirth,
            FramePresentSource.Dxgi, 0xA0, TimeSpan.FromSeconds(1), FrameTimePercentile.P95);
        var differentPercentile = Query(FrameTimePercentile.P99);

        foreach (var field in new[]
        {
            FrameFieldRegistry.PresentTimeActual, FrameFieldRegistry.PresentIntervalMs,
            FrameFieldRegistry.PresentedFps, FrameFieldRegistry.PresentIntervalP95Ms,
            FrameFieldRegistry.TimePercentileDerived
        })
        {
            Assert.NotEqual(first.Field(field).DeviceIdentity, differentWindow.Field(field).DeviceIdentity);
            Assert.NotEqual(first.Field(field).DeviceIdentity, differentPercentile.Field(field).DeviceIdentity);
        }
        Assert.NotEqual(first.PresentStreamIdentity, differentWindow.PresentStreamIdentity);
        Assert.NotEqual(first.FrameStreamIdentity, differentPercentile.FrameStreamIdentity);
    }

    private static double Number(IReadWriteBackend backend, string name)
        => backend.ReadCached(Query().Field(name)).Value?.Number
            ?? throw new InvalidOperationException($"Expected numeric frame field {name}.");

    private static double Number(IReadWriteBackend backend, PresentFrameFieldQuery query, string name)
        => backend.ReadCached(query.Field(name)).Value?.Number
            ?? throw new InvalidOperationException($"Expected numeric frame field {name}.");

    private static FakePresentSource SourceWithTargetStream(PresentFrameFieldQuery query, DateTimeOffset lastPresent)
        => new() { ReadHandler = _ => SnapshotWithIntervals(query, lastPresent, Intervals) };

    private static FrameTimingSnapshot SnapshotWithIntervals(
        PresentFrameFieldQuery query, DateTimeOffset lastPresent, double[] intervals)
    {
        var statistics = FrameIntervalStatistics.Compute(intervals)!;
        return new(Through, query.Window, true,
        [new(query.ProcessId, query.ProcessStartFileTimeUtc,
            [new(query.Source, query.SwapChain, lastPresent, statistics)])]);
    }

    private sealed class FakePresentSource : IFrameTimingObservationSource
    {
        private int acquisitions;
        private int activeLeases;
        public Func<TimeSpan, FrameTimingSnapshot?> ReadHandler { get; set; } = static _ => null;
        public Func<IDisposable>? AcquireHandler { get; set; }
        public int Acquisitions => Volatile.Read(ref acquisitions);
        public int ActiveLeases => Volatile.Read(ref activeLeases);
        public int ReadCount { get; private set; }
        public TimeSpan? LastWindow { get; private set; }

        public TimeSpan Retention => ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.FramePipeline.Retention;
        public IFrameSubscriptionLease AcquireSubscription(FrameSubscriptionRequest request) => new FrameLeaseAdapter(AcquireSubscription());

        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref acquisitions);
            if (AcquireHandler is { } handler) return handler();
            Interlocked.Increment(ref activeLeases);
            return new Lease(() => Interlocked.Decrement(ref activeLeases));
        }

        public FrameTimingSnapshot? Read(TimeSpan window)
        {
            ReadCount++;
            LastWindow = window;
            return ReadHandler(window);
        }

        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, []);

        private sealed class Lease(Action release) : IDisposable
        {
            private Action? release = release;
            public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
        }
    }

    private sealed class RejectingPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public int Attempts { get; private set; }
        public bool TryPublish(Action commit) { Attempts++; return false; }
    }

    private sealed class AcceptingPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }

    private sealed class CancelOnEnumerationProcessList(
        IReadOnlyList<FrameTimingProcess> processes, Action cancel) : IReadOnlyList<FrameTimingProcess>
    {
        public int Count => processes.Count;
        public FrameTimingProcess this[int index] => processes[index];
        public IEnumerator<FrameTimingProcess> GetEnumerator()
        {
            cancel();
            return processes.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
