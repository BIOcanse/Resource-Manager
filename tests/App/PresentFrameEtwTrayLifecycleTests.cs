using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameEtwTrayLifecycleTests
{
    private static readonly GpuPlacementProcessInstance Process =
        new(73, (ulong)DateTime.UnixEpoch.ToFileTimeUtc(), "recorded", "C:\\recorded.exe");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastHolderReleaseStopsThroughProductionActivityFactsWithoutHostShutdown(bool frames)
    {
        await using var fixture = PresentFrameEtwTestHarness.Create(useProductionActivityFacts: true);
        using var holder = frames ? fixture.Source.AcquireSubscription()
            : fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        holder.Dispose();
        await EtwTestRunRecorder.UntilAsync(() => session.Stops == 1);
        await fixture.Fixture.WaitForStateAsync(BackendUids.PresentFrameEtwSource, BusinessBlockState.Stopped);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.Empty(fixture.Source.ReadHolders());
        Assert.False(fixture.Source.HasActiveSubscriptions);
    }

    [Fact]
    public async Task EmptyRunReturnsAndALaterHolderStartsANewRunThroughProductionFacts()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create(useProductionActivityFacts: true);
        var reportedRunning = 0;
        await fixture.Source.RunAsync(new(default, () => reportedRunning++)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, reportedRunning);
        Assert.Empty(fixture.Sessions.Snapshot());
        using var holder = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        holder.Dispose();
        await EtwTestRunRecorder.UntilAsync(() => session.Stops == 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApiProvidersMatchTheirHolderAndIdentityFailureCannotArchiveAReusedPid(bool sameInstance)
    {
        var identityReads = 0;
        await using var fixture = PresentFrameEtwTestHarness.Create(useProductionActivityFacts: true,
            isCurrentGraphicsApiProcess: process => { identityReads++; Assert.Equal(Process, process); return sameInstance; });
        using var holder = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.DoesNotContain(PresentFrameEtwSource.KernelProcessProvider, session.Enabled);
        Assert.Equal(2, session.Enabled.Length);
        Assert.All(session.Operations, operation => Assert.Equal(new[] { Process.ProcessId }, operation.ProcessIds));
        // An unrelated delivery cannot cause an identity query. It is from a
        // requested runtime provider, but excluded by that provider's PID filter.
        Assert.False(fixture.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, 99, DateTime.UtcNow));
        Assert.Equal(0, identityReads);
        Assert.True(fixture.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42,
            Process.ProcessId, DateTime.UtcNow));
        Assert.Equal(1, identityReads);
        Assert.Equal(sameInstance ? GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12 : (GpuGraphicsApi?)null, holder.Read());
        await EtwTestRunRecorder.UntilAsync(() => session.Stops == 1);
        Assert.False(Assert.Single(fixture.Source.ReadHolders()).NeedsEvents);
    }
}
