using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class RunningGpuPlacementWindowBatchTests
{
    [Theory]
    [InlineData(GpuWindowActionMethod.Redraw)]
    [InlineData(GpuWindowActionMethod.Resize)]
    public async Task EachSelectedWindowRunsOnceWithTheSelectedMethodAndNoFallback(GpuWindowActionMethod method)
    {
        var requests = Requests(method);
        var calls = new List<GpuWindowActionRequest>();
        var batch = await WindowsRunningGpuPlacementActionService.ExecuteWindowRequestsAsync(requests,
            new(3, request => { calls.Add(request); return Task.FromResult(new RunningGpuPlacementWindowResult(GpuWindowActionOutcome.NotExecuted, null, true)); }, GpuRemoteCallTestOwner.RejectUnexpected, GpuRemoteCallTestOwner.RejectPreparation), default);
        Assert.Equal(requests, calls);
        Assert.Equal(3, batch.Results.Count);
        Assert.False(batch.Stopped);
        Assert.False(batch.LimitReached);
    }

    [Theory]
    [InlineData(GpuWindowActionOutcome.Unresolved, true)]
    [InlineData(GpuWindowActionOutcome.RestorationUnconfirmed, true)]
    [InlineData(GpuWindowActionOutcome.WindowRestored, false)]
    [InlineData(GpuWindowActionOutcome.RedrawRequested, false)]
    [InlineData(GpuWindowActionOutcome.NotExecuted, false)]
    public async Task UnresolvedOrOwnerHeldResultStopsBeforeTheNextWindow(GpuWindowActionOutcome outcome, bool allowed)
    {
        var calls = 0;
        var actual = new RunningGpuPlacementWindowResult(outcome, null, allowed);
        var batch = await WindowsRunningGpuPlacementActionService.ExecuteWindowRequestsAsync(Requests(),
            new(3, _ => { calls++; return Task.FromResult(actual); }, GpuRemoteCallTestOwner.RejectUnexpected, GpuRemoteCallTestOwner.RejectPreparation), default);
        Assert.Equal(1, calls);
        Assert.Same(actual, Assert.Single(batch.Results));
        Assert.True(batch.Stopped);
        Assert.False(batch.LimitReached);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationNeverStartsAnotherWindow(bool beforeFirst)
    {
        using var stop = new CancellationTokenSource();
        if (beforeFirst) stop.Cancel();
        var calls = 0;
        var batch = await WindowsRunningGpuPlacementActionService.ExecuteWindowRequestsAsync(Requests(),
            new(3, _ => { calls++; stop.Cancel(); return Task.FromResult(new RunningGpuPlacementWindowResult(GpuWindowActionOutcome.WindowRestored, null, true)); }, GpuRemoteCallTestOwner.RejectUnexpected, GpuRemoteCallTestOwner.RejectPreparation), stop.Token);
        Assert.Equal(beforeFirst ? 0 : 1, calls);
        Assert.Equal(calls, batch.Results.Count);
        Assert.True(batch.Stopped);
    }

    [Fact]
    public async Task ConfiguredWindowCapDoesNotStartTheTail()
    {
        var calls = 0;
        var batch = await WindowsRunningGpuPlacementActionService.ExecuteWindowRequestsAsync(Requests(),
            new(2, _ => { calls++; return Task.FromResult(new RunningGpuPlacementWindowResult(GpuWindowActionOutcome.WindowRestored, null, true)); }, GpuRemoteCallTestOwner.RejectUnexpected, GpuRemoteCallTestOwner.RejectPreparation), default);
        Assert.Equal(2, calls);
        Assert.Equal(2, batch.Results.Count);
        Assert.True(batch.LimitReached);
        Assert.False(batch.Stopped);
    }

    [Fact]
    public async Task TheSecondCallbackCannotStartWhileTheFirstIsPending()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RunningGpuPlacementWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = WindowsRunningGpuPlacementActionService.ExecuteWindowRequestsAsync(Requests(), new(3, _ =>
        {
            if (++calls == 1) { entered.SetResult(); return release.Task; }
            return Task.FromResult(new RunningGpuPlacementWindowResult(GpuWindowActionOutcome.WindowRestored, null, true));
        }, GpuRemoteCallTestOwner.RejectUnexpected, GpuRemoteCallTestOwner.RejectPreparation), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, calls);
            Assert.False(running.IsCompleted);
        }
        finally { release.TrySetResult(new(GpuWindowActionOutcome.WindowRestored, null, true)); }
        var batch = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, calls);
        Assert.False(batch.Stopped);
    }

    private static GpuWindowActionRequest[] Requests(GpuWindowActionMethod method = GpuWindowActionMethod.Resize)
        => [new(100, 133900000000000001, 1, method), new(100, 133900000000000001, 2, method), new(100, 133900000000000001, 3, method)];
}
