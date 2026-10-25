using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationSessionLifetimeTests
{
    [Fact]
    public async Task FailedStartupDoesNotRetryUntilDependencyIsWithdrawnAndAcquiredAgain()
    {
        var factory = new GpuAllocationTestSessionFactory { Failures = 1 };
        await using var harness = factory.Harness();
        var collector = harness.Collector;

        var failed = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<Exception>(() => failed.Ready);
        Assert.Equal(1, factory.Attempts);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);

        await failed.DisposeAsync();
        var next = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await next.Ready;
        Assert.Equal(2, factory.Attempts);
        Assert.Equal(0, factory.Current.Captures);
    }

    [Fact]
    public async Task UnexpectedExitFailsTheBlockAndCaptureDoesNotRestartIt()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await subscription.Ready;
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var old = factory.Current;
        var prior = collector.ReadCurrent(GpuAllocationTestSession.Processes);

        old.End(new IOException("injected consumer failure"));
        var completion = await harness.Controller.GetCompletion("gpu-allocation-etw");
        Assert.Equal(BusinessBlockOutcome.Failed, completion.Outcome);
        Assert.Equal(1, old.Stops);
        Assert.Equal(Timeout.InfiniteTimeSpan, old.StopTimeout);
        Assert.Same(prior, collector.ReadCurrent(GpuAllocationTestSession.Processes));

        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(1, factory.Attempts);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        old.End(new IOException("late callback"));
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));

        await subscription.DisposeAsync();
        var next = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await next.Ready;
        Assert.Equal(2, factory.Attempts);
    }

    [Fact]
    public async Task UnconfirmedSessionReleaseBlocksAReplacementRun()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var first = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await first.Ready;
        factory.Current.StopResult = new(true, false, true, ["fake disposal failure"]);
        factory.Current.End();
        Assert.Equal(BusinessBlockOutcome.Failed,
            (await harness.Controller.GetCompletion("gpu-allocation-etw")).Outcome);
        await first.DisposeAsync();

        var next = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<Exception>(() => next.Ready);
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Stops);
    }

    [Fact]
    public async Task PartialFactoryCleanupFailureBlocksReplacementRun()
    {
        var factory = new GpuAllocationTestSessionFactory { PartialCleanupFailure = true };
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var first = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<Exception>(() => first.Ready);
        await first.DisposeAsync();

        var next = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<Exception>(() => next.Ready);
        Assert.Equal(1, factory.Attempts);
        Assert.Empty(factory.Sessions);
    }
}
