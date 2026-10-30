using ResourceManager.App.Application.ResourceBreakdown;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using ResourceManager.ReadWrite.Windows.Processes;
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
    public async Task CaptureHoldReturnsNativeResourcesOnEveryExit(string exit)
    {
        using var broker = new Broker();
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture);
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("capture hold failed");
        PhysicalDiskIoAttributionSnapshot? published = null;
        AssertReleased(broker);

        var run = reader.RunCaptureHoldAsync(async () =>
        {
            tick = 3100;
            Feed(broker, tick);
            AssertHeld(broker, capture);
            await Task.Yield();
            // The same scope remains live across an asynchronous hold.
            AssertHeld(broker, capture);
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
                AssertHeld(broker, capture);
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

        AssertReleased(broker);
        Assert.Null(capture.Read(true).ObservedTraffic);
        Assert.Equal(0, broker.ActiveSubscriptions);
        if (exit == "publish")
        {
            Assert.NotNull(published);
            AssertPublished(published);
        }
    }

    [Fact]
    public async Task NormalCaptureKeepsBucketsUntilTheHoldReturns()
    {
        using var broker = new Broker();
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture);

        await reader.RunCaptureHoldAsync(async () =>
        {
            tick = 3100;
            Feed(broker, tick);
            AssertHeld(broker, capture);
            await Task.Yield();
            AssertHeld(broker, capture);
            AssertPublished(reader.Read(includeObservedTraffic: true));
        });

        AssertReleased(broker);
        Assert.Null(capture.Read(true).ObservedTraffic);
        Assert.Equal(0, broker.ActiveSubscriptions);
    }

    [Fact]
    public async Task SubscriptionStopFailureStillReturnsNativeResources()
    {
        var failure = new IOException("subscription stop failed");
        using var broker = new Broker { StopFailure = failure };
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture);

        var error = await Assert.ThrowsAsync<IOException>(() => reader.RunCaptureHoldAsync(() =>
        {
            tick = 3100;
            Feed(broker, tick);
            AssertHeld(broker, capture);
            return Task.CompletedTask;
        }));

        Assert.Same(failure, error);
        AssertReleased(broker);
        Assert.Null(capture.Read(true).ObservedTraffic);
        Assert.Equal(0, broker.ActiveSubscriptions);
    }

    [Fact]
    public async Task ASecondHoldAcquiresFreshBucketsAfterThePreviousScopeReturns()
    {
        using var broker = new Broker();
        long tick = 1000;
        var capture = NewCapture(broker, () => tick);
        using var reader = new EtwPhysicalDiskIoAttributionReader(capture);

        for (var run = 0; run < 2; run++)
        {
            await reader.RunCaptureHoldAsync(() =>
            {
                AssertHeld(broker, capture);
                Assert.Empty(reader.Read(true).Processes);
                tick += 2100;
                Feed(broker, tick);
                AssertHeld(broker, capture);
                AssertPublished(reader.Read(includeObservedTraffic: true));
                return Task.CompletedTask;
            });
            AssertReleased(broker);
            Assert.Null(capture.Read(true).ObservedTraffic);
            Assert.Equal(0, broker.ActiveSubscriptions);
        }
        Assert.Equal(2, broker.Bindings);
    }

    private static EtwPhysicalDiskIoCaptureOwner NewCapture(Broker broker, Func<long> clock)
        => new(broker, new(), clock);

    private static void Feed(Broker broker, long tick)
    {
        broker.Resource.Disk(tick, 41, 3, 4096, false, 100);
        broker.Resource.Disk(tick, 41, 3, 8192, true, 100);
        broker.Resource.Disk(tick, 41, 3, 2048, false, 100);
        broker.Resource.Publish(tick);
    }

    private static void AssertHeld(Broker broker, EtwPhysicalDiskIoCaptureOwner capture)
    {
        Assert.True(capture.IsRunning);
        Assert.True(broker.Resource.RegionUsage.CurrentBytes > 0);
        Assert.Equal(1, broker.ActiveSubscriptions);
    }

    private static void AssertReleased(Broker broker)
    {
        Assert.Equal(0UL, broker.Resource.RegionUsage.CurrentBytes);
        Assert.Equal(0, broker.ActiveSubscriptions);
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

    // Uses the real shared native owner and its bounded region allocation.
    // The stop fault occurs only after the native hold has actually returned.
    private sealed class Broker : IKernelEtwSessionBroker, IDisposable
    {
        internal NativeKernelEtwTestResource Resource { get; } = new(NativeKernelEtwTestResource.SmallConfiguration());
        internal int ActiveSubscriptions;
        internal int Bindings;
        internal Exception? StopFailure;
        internal Broker() => Resource.Identities[41] = new(100);
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            var held = Resource.Acquire(request);
            Bindings++;
            ActiveSubscriptions++;
            return new Subscription(this, held);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => Resource.GetSnapshot();
        public void Dispose() => Resource.Dispose();

        private sealed class Subscription(Broker broker, IKernelEtwSubscription held) : IKernelEtwSubscription
        {
            private bool disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => held.GetSnapshot();
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                held.Dispose();
                broker.ActiveSubscriptions--;
                if (broker.StopFailure is { } failure) throw failure;
            }
        }
    }
}
