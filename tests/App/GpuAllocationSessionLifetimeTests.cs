using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationSessionLifetimeTests
{
    [Fact]
    public async Task FailedStartupRetriesOnlyAtNextDueCaptureNeverOnRead()
    {
        var factory = new GpuAllocationTestSessionFactory { Failures = 1 };
        using var collector = factory.Collector();
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        Assert.Equal(0, factory.Attempts);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        for (var i = 0; i < 10; i++) Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Attempts);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(2, factory.Attempts);
        Assert.Empty(factory.Current.Enabled);
    }

    [Fact]
    public async Task UnexpectedExitReleasesOldRuntimeBeforeReplacementAndIgnoresLateCallbacks()
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector();
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var old = factory.Current;
        var prior = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        old.End();
        Assert.Same(prior, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Attempts);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, old.Stops);
        Assert.Equal(2, factory.Attempts);
        var replacement = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        old.End(new IOException("late callback"));
        Assert.Same(replacement, collector.ReadCurrent(GpuAllocationTestSession.Processes));
    }

    [Fact]
    public async Task UnconfirmedReleaseCannotCreateSecondRuntime()
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector();
        var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        factory.Current.StopResult = new(true, false, true, ["fake disposal failure"]);
        factory.Current.End();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        lease.Dispose();
        using var next = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Stops);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
    }

    [Fact]
    public async Task PartialFactoryCleanupFailureBlocksReplacement()
    {
        var factory = new GpuAllocationTestSessionFactory { PartialCleanupFailure = true };
        using var collector = factory.Collector();
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
    }

    [Fact]
    public async Task LastLeaseStopsPendingCaptureAndDisposeCannotRestart()
    {
        var factory = new GpuAllocationTestSessionFactory { Configure = session => session.HoldEnd = true };
        using var collector = factory.Collector();
        var first = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        var last = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        var capture = collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        first.Dispose();
        Assert.Equal(0, factory.Current.Stops);
        last.Dispose();
        await capture.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, factory.Current.Stops);
        Assert.Empty(factory.Current.Enabled);
        factory.Current.DeliverEnd();
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        collector.Dispose();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
        Assert.Throws<ObjectDisposedException>(() => collector.AcquireSubscription(TimeSpan.FromSeconds(1)));
    }
}
