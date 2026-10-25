namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationRescanTests
{
    [Fact]
    public async Task CompleteSnapshotReplacesAtomicallyAndEventsOutsideBoundariesCannotChangeIt()
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector();
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var old = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(1024, old.Adapters[23][41].PrivateBytes);
        Assert.Empty(factory.Current.Enabled);
        factory.Current.Emit(34, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL));
        Assert.Same(old, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        factory.Current.Bytes = 2048;
        factory.Current.HoldEnd = true;
        var capture = collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.False(capture.IsCompleted);
        Assert.Same(old, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        factory.Current.DeliverEnd();
        await capture;
        var next = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(2048, next.Adapters[23][41].PrivateBytes);
        Assert.Equal(1024, old.Adapters[23][41].PrivateBytes);
        Assert.Empty(factory.Current.Enabled);
        Assert.Equal(2, factory.Current.Captures);
    }

    [Theory]
    [InlineData("loss")]
    [InlineData("capture")]
    [InlineData("malformed")]
    [InlineData("incomplete")]
    [InlineData("timeout")]
    [InlineData("disable")]
    public async Task FailedDuePublishesEmptyAndNextDueRecovers(string failure)
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector(TimeSpan.FromMilliseconds(40));
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var session = factory.Current;
        session.LoseEvents = failure == "loss";
        session.FailCapture = failure == "capture";
        session.Malformed = failure == "malformed";
        session.Incomplete = failure == "incomplete";
        session.HoldEnd = failure == "timeout";
        session.FailDisable = failure == "disable";
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Empty(session.Enabled);
        Assert.Equal(2, session.Captures);
        session.DeliverEnd(); // A late end cannot publish or complete the next capture.
        session.LoseEvents = session.FailCapture = session.Malformed = session.Incomplete = session.HoldEnd = session.FailDisable = false;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Empty(factory.Current.Enabled);
    }

    [Fact]
    public async Task ReusedPidCannotReceiveThePublishedOwnersMemory()
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector();
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var reused = collector.ReadCurrent(new Dictionary<int, long> { [41] = 101 })!;
        Assert.Empty(reused.Adapters[23]);
        Assert.Equal(1024, collector.ReadCurrent(GpuAllocationTestSession.Processes)!.Adapters[23][41].PrivateBytes);
    }
}
