using System.Collections.Immutable;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class HostCpuPeriodicSubscriptionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task CpuTopologyFieldLeasesShareTheOriginalNativeItemAndReleaseAtTheirOwnPeriods()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        var backend = CreateTopologyBackend(fixture.Reader);
        fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        await backend.DiscoverAsync(CancellationToken.None);
        var field = SelectPhysicalCoreCount(backend);
        var capturesBeforeDemand = fixture.Sampler.CaptureCount;

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Reader.AcquireFieldSubscription(
            "below-role-minimum", [field], TimeSpan.FromMilliseconds(99),
            FieldSubscriptionOptions.Default, backend));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Reader.AcquireFieldSubscription(
            "fractional-millisecond", [field], TimeSpan.FromTicks(1),
            FieldSubscriptionOptions.Default, backend));
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);

        using var slow = fixture.Reader.AcquireFieldSubscription("slow-field", [field],
            TimeSpan.FromMilliseconds(500), FieldSubscriptionOptions.Default, backend);
        using var fast = fixture.Reader.AcquireFieldSubscription("fast-field", [field],
            TimeSpan.FromMilliseconds(100), FieldSubscriptionOptions.Default, backend);

        Assert.Equal((2u, 2u, 1u, 100ul), fixture.ReadDemand());

        fast.Dispose();
        Assert.Equal((1u, 1u, 1u, 500ul), fixture.ReadDemand());

        slow.Dispose();
        // The native planner retains the known item identity after its last demand is removed.
        Assert.Equal((0u, 0u, 1u, 0ul), fixture.ReadDemand());
        Assert.Equal(0u, fixture.Owner.Execute(CpuTopologySnapshotProvider.SamplingRoleId, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return header.MembershipCount;
        }));
        Assert.Equal(capturesBeforeDemand, fixture.Sampler.CaptureCount);
    }

    [Fact]
    public async Task FreshCpuTopologySubscriptionUsesCommittedOriginalCaptureAndCancellationReleasesDemand()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        var backend = CreateTopologyBackend(fixture.Reader);
        var cached = CpuTopologyInformationFieldTests.Snapshot();
        fixture.Sampler.Complete(cached);
        await backend.DiscoverAsync(CancellationToken.None);
        var field = SelectPhysicalCoreCount(backend);
        Assert.Equal("2", backend.ReadCached(field).Value!.Integer);
        using var cancellation = new CancellationTokenSource(Deadline);
        await using var stream = fixture.Reader.SubscribeFieldsAsync("fresh-topology", [field],
                TimeSpan.FromMilliseconds(100),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), backend, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var first = stream.MoveNextAsync().AsTask();
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        Assert.Same(cached, fixture.Reader.GetSnapshot());
        var capturesBeforeWorker = fixture.Sampler.CaptureCount;

        await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        var recorded = CpuTopologyInformationFieldTests.Snapshot() with
        {
            CapturedAt = cached.CapturedAt.AddSeconds(1),
            OsTopologyInformation = new([], [])
        };
        fixture.Sampler.Complete(recorded);

        Assert.True(await first.WaitAsync(Deadline));
        var reading = Assert.Single(stream.Current);
        Assert.Equal(field, reading.Field);
        Assert.Equal(FieldObservationStatus.Current, reading.Status);
        Assert.Equal("0", reading.Value!.Integer);
        Assert.Equal(recorded.CapturedAt, reading.ObservedAt);
        Assert.Equal(fixture.Reader.GetInformationSnapshot().ObservedAt, reading.ObservedAt);
        Assert.Equal(capturesBeforeWorker + 1, fixture.Sampler.CaptureCount);

        var resetsBeforeCancel = fixture.Sampler.ResetCount;
        await cancellation.CancelAsync();
        await Task.Delay(250, CancellationToken.None);
        Assert.Equal(capturesBeforeWorker + 1, fixture.Sampler.CaptureCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.MoveNextAsync().AsTask());
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        await WaitUntilAsync(() => fixture.Sampler.ResetCount > resetsBeforeCancel);
        Assert.Same(recorded, fixture.Reader.GetSnapshot());
    }

    [Fact]
    public async Task CpuTopologyFreshResultIsCapturedInsideTheOriginalCommitBeforeDirectCapture()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        using var backend = new BlockingCurrentBackend(CreateTopologyBackend(fixture.Reader));
        using var cancellation = new CancellationTokenSource(Deadline);
        var cached = CpuTopologyInformationFieldTests.Snapshot();
        fixture.Sampler.Complete(cached);
        await backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        var field = SelectPhysicalCoreCount(backend);
        await using var stream = fixture.Reader.SubscribeFieldsAsync("fresh-topology-commit", [field],
                TimeSpan.FromMilliseconds(5000), new(FieldSubscriptionSamplingMode.FreshOnTrigger),
                backend, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var first = stream.MoveNextAsync().AsTask();
        var capturesBeforeWorker = fixture.Sampler.CaptureCount;

        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(capturesBeforeWorker + 1, await fixture.Sampler.WaitForCaptureAsync());
        var committed = cached with
        {
            CapturedAt = cached.CapturedAt.AddSeconds(1),
            OsTopologyInformation = new([], [])
        };
        backend.BlockNextRead();
        fixture.Sampler.Complete(committed);
        await backend.WaitForBlockedReadAsync();

        // A direct source read now waits on the original capture/publication gates while the
        // committed fresh field snapshot is still being read inside its accepted action.
        var directStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directCapture = Task.Run(async () =>
        {
            directStarted.SetResult();
            return await fixture.Reader.CaptureInformationAsync(CancellationToken.None);
        });
        await directStarted.Task.WaitAsync(Deadline);
        await Task.Delay(50, CancellationToken.None);
        Assert.False(directCapture.IsCompleted);
        Assert.Equal(capturesBeforeWorker + 1, fixture.Sampler.CaptureCount);
        backend.ReleaseBlockedRead();

        Assert.True(await first.WaitAsync(Deadline));
        var fresh = Assert.Single(stream.Current);
        Assert.Equal(FieldObservationStatus.Current, fresh.Status);
        Assert.Equal("0", fresh.Value!.Integer);
        Assert.Equal(committed.CapturedAt, fresh.ObservedAt);

        Assert.Equal(capturesBeforeWorker + 2, await fixture.Sampler.WaitForCaptureAsync());
        var laterCurrent = cached with
        {
            CapturedAt = cached.CapturedAt.AddSeconds(2),
            OsTopologyInformation = new([
                new("core:0", 0, true, [0]),
                new("core:1", 0, true, [1]),
                new("core:2", 0, true, [2])], [])
        };
        fixture.Sampler.Complete(laterCurrent);
        _ = await directCapture.WaitAsync(Deadline);
        Assert.Equal("3", backend.ReadCached(field).Value!.Integer);
        Assert.Equal("0", fresh.Value.Integer);

        await cancellation.CancelAsync();
        await Task.Delay(250, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.MoveNextAsync().AsTask());
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    [Fact]
    public async Task FreshCpuResidencySubscriptionDoesNotRelabelCachedValueWhileOriginalEtwWindowIsWarming()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new WarmingBroker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        var backend = new RecordedResidencyBackend();
        var field = backend.Field;
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.AcquireFieldSubscription(
            "below-role-minimum", [field], TimeSpan.FromMilliseconds(99),
            FieldSubscriptionOptions.Default, backend));
        using var cancellation = new CancellationTokenSource(Deadline);
        await using var stream = reader.SubscribeFieldsAsync("fresh-residency", [field],
                TimeSpan.FromMilliseconds(100),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), backend, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var first = stream.MoveNextAsync().AsTask();
        Assert.Equal(1u, sampling.Owner.Execute(6, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return header.ActiveSourceCount;
        }));

        await reader.StartAsync(CancellationToken.None);
        Assert.True(await first.WaitAsync(Deadline));
        var reading = Assert.Single(stream.Current);
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value);
        Assert.Null(reading.ObservedAt);
        Assert.Equal("windows.etw.cpu-residency", reading.SourceId);
        Assert.Equal("Warming", reader.GetDiagnostics().State);
        Assert.Equal(1, broker.SubscribeCount);
        Assert.True(backend.CachedReadCount > 0); // A cached control value is not evidence of a closed ETW window.

        await cancellation.CancelAsync();
        await Task.Delay(250, CancellationToken.None);
        Assert.Equal(1, broker.SubscribeCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.MoveNextAsync().AsTask());
        Assert.Equal(0u, sampling.Owner.Execute(6, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return header.ActiveSourceCount;
        }));
        await reader.StopAsync(CancellationToken.None);
        Assert.Equal(1, broker.DisposeCount);
    }

    private static InformationReadWriteBackend CreateTopologyBackend(CpuTopologySnapshotProvider reader)
        => new(reader.GetInformationSnapshot, reader.CaptureInformationAsync,
            static _ => new OperationSupport(true, null, []));

    private static FieldReference SelectPhysicalCoreCount(IReadWriteBackend backend)
        => backend.Catalog.Fields.Single(static capability =>
            capability.Field.FieldName == "cpu_all_physical_core_count_reported").Field;

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class RecordedResidencyBackend : IReadWriteBackend
    {
        private int cachedReadCount;

        internal FieldReference Field { get; } = new(
            "windows-process|741:638641234567890123|logical-cpu:0",
            "process_all_cpu_residency_attributed");
        internal int CachedReadCount => Volatile.Read(ref cachedReadCount);
        public ReadWriteCatalog Catalog { get; }

        internal RecordedResidencyBackend()
        {
            Catalog = new(
                [new DeviceDescriptor(Field.DeviceIdentity, "process", "all")],
                [new FieldCapability(Field,
                    new FieldDescriptor(Field.FieldName, "%", "float64", "recorded original CPU residency current"),
                    new OperationSupport(true, null, []),
                    OperationSupport.Unsupported("read-only-test-current"),
                    "windows.etw.cpu-residency")]);
        }

        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Catalog);

        public FieldReading ReadCached(FieldReference field)
        {
            Interlocked.Increment(ref cachedReadCount);
            return field == Field
                ? new(Field, FieldObservationStatus.Current, new(Number: 37.5), DateTimeOffset.UnixEpoch,
                    "windows.etw.cpu-residency", 5000)
                : throw new ArgumentException("Unknown recorded residency field.", nameof(field));
        }

        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The original ETW reader owns collection.");

        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new FieldWriteResult(field, "unsupported", "read-only-test-current"));
    }

    private sealed class BlockingCurrentBackend(IReadWriteBackend inner) : IReadWriteBackend, IDisposable
    {
        private readonly ManualResetEventSlim readEntered = new(false);
        private readonly ManualResetEventSlim releaseRead = new(false);
        private int blockNextRead;

        public ReadWriteCatalog Catalog => inner.Catalog;

        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
            => inner.DiscoverAsync(cancellationToken);

        public FieldReading ReadCached(FieldReference field)
        {
            if (Interlocked.Exchange(ref blockNextRead, 0) != 0)
            {
                readEntered.Set();
                if (!releaseRead.Wait(Deadline))
                    throw new TimeoutException("The original commit read was not released.");
            }

            return inner.ReadCached(field);
        }

        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields,
            CancellationToken cancellationToken)
            => inner.CollectAsync(fields, cancellationToken);

        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value,
            CancellationToken cancellationToken)
            => inner.WriteAsync(field, value, cancellationToken);

        internal void BlockNextRead()
        {
            readEntered.Reset();
            releaseRead.Reset();
            Volatile.Write(ref blockNextRead, 1);
        }

        internal Task WaitForBlockedReadAsync()
            => Task.Run(() => Assert.True(readEntered.Wait(Deadline))).WaitAsync(Deadline);

        internal void ReleaseBlockedRead() => releaseRead.Set();

        public void Dispose()
        {
            releaseRead.Set();
            releaseRead.Dispose();
            readEntered.Dispose();
        }
    }

    private sealed class WarmingBroker : IKernelEtwSessionBroker
    {
        private int subscribeCount;
        private int disposeCount;
        internal int SubscribeCount => Volatile.Read(ref subscribeCount);
        internal int DisposeCount => Volatile.Read(ref disposeCount);

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            var source = new KernelEtwEventSource();
            Task.Run(() => request.Bind(source, new(37, DateTimeOffset.UnixEpoch)))
                .GetAwaiter().GetResult();
            source.Activate();
            return new RecordedLease(this, source);
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new(SubscribeCount > DisposeCount ? "Running" : "Unavailable",
                "Recorded fixture: no context-switch events were supplied.", 37, DateTimeOffset.UnixEpoch,
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                    | KernelTraceEventParser.Keywords.ContextSwitch,
                SubscribeCount - DisposeCount, 0);

        private sealed class RecordedLease(WarmingBroker owner, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                source.Cancel();
                Interlocked.Increment(ref owner.disposeCount);
            }
        }
    }
}
