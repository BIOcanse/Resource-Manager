namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationRescanTests
{
    [Fact]
    public async Task CompleteSnapshotReplacesAtomicallyAndEventsOutsideBoundariesCannotChangeIt()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var old = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(1024, old.Adapters[23][41].PrivateBytes);
        Assert.Empty(factory.Current.Enabled);
        factory.Current.Emit(34, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL));
        Assert.Same(old, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        factory.Configure = session => { session.Bytes = 2048; session.HoldEnd = true; };
        var capture = collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.False(capture.IsCompleted);
        Assert.Same(old, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        factory.Current.DeliverEnd();
        await capture;
        var next = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(2048, next.Adapters[23][41].PrivateBytes);
        Assert.Equal(1024, old.Adapters[23][41].PrivateBytes);
        Assert.Empty(factory.Current.Enabled);
        Assert.Equal(2, factory.Sessions.Sum(session => session.Captures));
        Assert.All(factory.Sessions, session => Assert.True(session.Joined));
    }

    [Theory]
    [InlineData("loss")]
    [InlineData("malformed")]
    [InlineData("incomplete")]
    [InlineData("timeout")]
    public async Task FailedDuePublishesEmptyAndNextDueRecovers(string failure)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(TimeSpan.FromMilliseconds(40));
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        factory.Configure = session =>
        {
            session.LoseEvents = failure == "loss";
            session.Malformed = failure == "malformed";
            session.Incomplete = failure == "incomplete";
            session.HoldEnd = failure == "timeout";
        };
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var session = factory.Current;
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Empty(session.Enabled);
        Assert.Equal(1, session.Captures);
        Assert.True(session.Joined);
        Assert.Equal(2, factory.Sessions.Sum(value => value.Captures));
        session.DeliverEnd(); // A late end cannot publish or complete the next capture.
        factory.Configure = null;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Empty(factory.Current.Enabled);
    }

    [Fact]
    public async Task ReusedPidCannotReceiveThePublishedOwnersMemory()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var reused = collector.ReadCurrent(new Dictionary<int, long> { [41] = 101 })!;
        Assert.Empty(reused.Adapters[23]);
        Assert.Equal(1024, collector.ReadCurrent(GpuAllocationTestSession.Processes)!.Adapters[23][41].PrivateBytes);
    }
}
