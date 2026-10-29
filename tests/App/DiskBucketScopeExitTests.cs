using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class DiskBucketScopeExitTests
{
    [Theory]
    [InlineData("skip")]
    [InlineData("cancel")]
    [InlineData("throw")]
    [InlineData("publish")]
    public async Task CaptureHoldReturnsRealLedgerBucketsOnEveryExit(string exit)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var broker = new Broker();
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture, ledger);
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("capture hold failed");
        PhysicalDiskIoAttributionSnapshot? published = null;
        AssertReleased(ledger);

        var run = reader.RunCaptureHoldAsync(async () =>
        {
            tick = 3100;
            Feed(capture, tick);
            AssertHeld(ledger, capture);
            await Task.Yield();
            // The same scope remains live across an asynchronous hold.
            AssertHeld(ledger, capture);
            if (exit == "cancel")
            {
                cancellation.Cancel();
                await Task.FromCanceled(cancellation.Token);
            }
            if (exit == "throw") throw failure;
            if (exit == "publish")
            {
                published = reader.Read(includeObservedTraffic: true);
                AssertPublished(published);
                AssertHeld(ledger, capture);
            }
            // The skip path returns without publishing a selected window.
        });

        if (exit == "cancel")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else if (exit == "throw")
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => run));
        else await run;

        AssertReleased(ledger);
        Assert.Equal(0, capture.RetainedPairCount);
        Assert.Equal(0, broker.ActiveSubscriptions);
        if (exit == "publish")
        {
            Assert.NotNull(published);
            AssertPublished(published);
        }
    }

    [Fact]
    public async Task FreezeReturnsBucketsImmediatelyAndResumeUsesAFreshWindowInsideTheHold()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var broker = new Broker();
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture, ledger);

        await reader.RunCaptureHoldAsync(async () =>
        {
            tick = 3100;
            Feed(capture, tick);
            AssertHeld(ledger, capture);
            reader.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
            AssertReleased(ledger);
            Assert.Equal(0, capture.RetainedPairCount);
            Assert.Equal(0, broker.ActiveSubscriptions);
            var frozen = reader.Read(includeObservedTraffic: true);
            Assert.Equal(PhysicalDiskIoProviderStates.Frozen, frozen.ProviderState.State);
            Assert.Empty(frozen.Processes);
            Assert.Null(frozen.ObservedTraffic);
            await Task.Yield();
            AssertReleased(ledger);

            reader.ApplyMode(ResourceManagerComputeZoneMode.Normal);
            Assert.Equal(2, broker.Bindings);
            AssertReleased(ledger);
            Assert.Empty(reader.Read().Processes);
            tick = 5200;
            Feed(capture, tick);
            AssertHeld(ledger, capture);
            AssertPublished(reader.Read(includeObservedTraffic: true));
        });

        AssertReleased(ledger);
        Assert.Equal(0, capture.RetainedPairCount);
        Assert.Equal(0, broker.ActiveSubscriptions);
    }

    [Fact]
    public async Task SubscriptionStopFailureStillReturnsRealLedgerBuckets()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var failure = new IOException("subscription stop failed");
        var broker = new Broker { StopFailure = failure };
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture, ledger);

        var error = await Assert.ThrowsAsync<IOException>(() => reader.RunCaptureHoldAsync(() =>
        {
            tick = 3100;
            Feed(capture, tick);
            AssertHeld(ledger, capture);
            return Task.CompletedTask;
        }));

        Assert.Same(failure, error);
        AssertReleased(ledger);
        Assert.Equal(0, capture.RetainedPairCount);
        Assert.Equal(0, broker.ActiveSubscriptions);
    }

    [Fact]
    public async Task ASecondHoldAcquiresFreshBucketsAfterThePreviousScopeReturns()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var broker = new Broker();
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture, ledger);

        for (var run = 0; run < 2; run++)
        {
            await reader.RunCaptureHoldAsync(() =>
            {
                AssertReleased(ledger);
                tick += 2100;
                Feed(capture, tick);
                AssertHeld(ledger, capture);
                AssertPublished(reader.Read(includeObservedTraffic: true));
                return Task.CompletedTask;
            });
            AssertReleased(ledger);
            Assert.Equal(0, capture.RetainedPairCount);
            Assert.Equal(0, broker.ActiveSubscriptions);
        }
        Assert.Equal(2, broker.Bindings);
    }

    private static EtwPhysicalDiskIoCaptureOwner NewCapture(Broker broker, Func<long> clock)
        => new(broker, new(), clock, pid => new ProcessLease(pid), bucketWidthMilliseconds: 1);

    private static void Feed(EtwPhysicalDiskIoCaptureOwner capture, long tick)
    {
        capture.ObserveDiskTransfer(41, 4096UL, false, tick, 3, 100);
        capture.ObserveDiskTransfer(41, 8192UL, true, tick, 3, 100);
        capture.ObserveDiskTransfer(41, 2048UL, false, tick, 3, 100);
    }

    private static void AssertHeld(ResourceManagerLedgerOwner ledger, EtwPhysicalDiskIoCaptureOwner capture)
    {
        var diagnostics = ledger.ReadCaptureBucketDiagnostics();
        Assert.Equal(2, diagnostics.AcquiredLeaseCount);
        Assert.True(diagnostics.CommittedBucketBytes > ledger.NativePageSize);
        var bytesPerChunk = (ulong)capture.RetainedBucketBytes / 2;
        var pageBytes = ((bytesPerChunk + ledger.NativePageSize - 1) / ledger.NativePageSize) * ledger.NativePageSize;
        Assert.Equal(2 * pageBytes, diagnostics.CommittedBucketBytes);
        Assert.Equal(ledger.ReadTable().SystemPageBytes, diagnostics.CommittedBucketBytes);
    }

    private static void AssertReleased(ResourceManagerLedgerOwner ledger)
    {
        var diagnostics = ledger.ReadCaptureBucketDiagnostics();
        Assert.Equal(0, diagnostics.AcquiredLeaseCount);
        Assert.Equal(0UL, diagnostics.CommittedBucketBytes);
        Assert.Equal(0UL, ledger.ReadTable().SystemPageBytes);
    }

    private static void AssertPublished(PhysicalDiskIoAttributionSnapshot snapshot)
    {
        Assert.Equal(PhysicalDiskIoProviderStates.Running, snapshot.ProviderState.State);
        var process = Assert.Single(snapshot.Processes).Value;
        Assert.Equal(41, process.ProcessId);
        Assert.Equal(3072, process.ReadBytesPerSecond);
        Assert.Equal(4096, process.WriteBytesPerSecond);
        Assert.Equal(2, process.ReadEventCount);
        Assert.Equal(1, process.WriteEventCount);
        var observed = Assert.IsType<PhysicalDiskProcessIoWindow>(snapshot.ObservedTraffic);
        Assert.True(observed.Complete);
        var row = Assert.Single(observed.Processes);
        Assert.Equal("6144", row.ReadBytes);
        Assert.Equal("8192", row.WriteBytes);
    }

    private sealed class ProcessLease(uint pid) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId => pid;
        public bool TryReadCreationFileTime(out ulong birth) { birth = 100; return true; }
        public void Dispose() { }
    }

    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal int ActiveSubscriptions;
        internal int Bindings;
        internal Exception? StopFailure;
        private Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords keywords;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            var source = new KernelEtwEventSource();
            request.Bind(source, new(17, DateTimeOffset.UnixEpoch));
            source.Activate();
            keywords = request.Keywords;
            Bindings++;
            ActiveSubscriptions++;
            return new Subscription(this, source);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new(
            ActiveSubscriptions == 0 ? "Idle" : "Running", "synthetic disk scope session", 17,
            DateTimeOffset.UnixEpoch, keywords, ActiveSubscriptions, 0);

        private sealed class Subscription(Broker broker, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            private bool disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                source.Cancel();
                broker.ActiveSubscriptions--;
                if (broker.StopFailure is { } failure) throw failure;
            }
        }
    }
}
