using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationSubscriptionLifetimeTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("enable")]
    [InlineData("capture")]
    [InlineData("disable")]
    [InlineData("consumer")]
    public async Task CaptureWhileFieldRemainsInUseAfterPermissionFailureReturnsEmptyWithoutRestarting(string operation)
    {
        var denial = new UnauthorizedAccessException("injected GPU " + operation + " permission failure");
        var factory = new GpuAllocationTestSessionFactory();
        if (operation == "create") factory.CreateFailure = denial;
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        if (operation != "create")
        {
            await harness.WaitRunningAsync();
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
            Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
            var session = factory.Current;
            if (operation == "consumer") session.End(denial);
            else
            {
                if (operation == "enable") session.EnableFailure = denial;
                else if (operation == "capture") session.CaptureFailure = denial;
                else session.DisableFailure = denial;
                await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
            }
        }

        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await harness.Fixture.WaitForStateAsync(BackendUids.GpuAllocationCollector, BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.GpuAllocationCollector));
        Assert.False(harness.Fixture.Authority.Read<bool>(StateItemUids.GpuAllocationNativeSessionAvailable).Value);
        Assert.True(harness.Fixture.Authority.Read<bool>(StateItemUids.ProcessDedicatedGpuMemoryInUse).Value);
        var captures = factory.Sessions.Sum(session => session.Captures);
        for (var index = 0; index < 2; index++)
        {
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        }
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(captures, factory.Sessions.Sum(session => session.Captures));
        Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
    }

    [Fact]
    public async Task CaptureWhileTheNativeRunIsStoppingReturnsEmptyWithoutWaitingForJoin()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        var session = factory.Current;
        using var joinGate = new ManualResetEventSlim();
        var joining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeJoin = () => { joining.TrySetResult(); Assert.True(joinGate.Wait(TimeSpan.FromSeconds(10))); };
        var stopping = harness.StopAsync();
        try
        {
            await joining.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BusinessBlockState.Stopping, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
            Assert.False(session.Joined);
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
            Assert.Equal(1, factory.Attempts);
            Assert.Equal(1, session.Captures);
            Assert.False(session.Joined);
            Assert.False(stopping.IsCompleted);
        }
        finally { joinGate.Set(); }
        await stopping;
        Assert.True(session.Joined);
    }

    [Fact]
    public async Task CaptureWithoutARunReturnsEmptyAndAFieldBecomingInUseStartsAFreshRun()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        await harness.StopAsync();
        await harness.NextResultAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Attempts);

        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(2, factory.Attempts);
        Assert.Equal(1, factory.Current.Captures);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
    }
}
