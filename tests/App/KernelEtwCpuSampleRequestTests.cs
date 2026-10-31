using System.Collections.Frozen;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

/// <summary>Existing CPU collection over actual shared commands and CPU component acknowledgments.</summary>
public sealed class KernelEtwCpuSampleRequestTests
{
    [Fact]
    public async Task ForcedFixturePublicationIsImmediatelyReusableAndWarmingRemainsAnAcceptedSample()
    {
        using var run = new Run();
        Assert.True(await run.Owner.CollectAsync());
        Assert.Null(run.Owner.Read());
        run.Close(0, 1000);
        run.Resource.Publish(1000);
        var operation = run.Owner.CollectAsync();
        Assert.True(operation.IsCompletedSuccessfully); // No extra fixture pump is permitted.
        Assert.True(await operation);
        Assert.Equal(1000, Assert.Single(run.Owner.Read()!.Processes).ExecutionTimeMilliseconds);
        var current = run.Owner.Read();
        for (var i = 0; i < 50; ++i) Assert.Same(current, run.Owner.Read());
        Assert.Equal(1, run.Resource.Creations); Assert.Equal(0, run.Resource.Stops);
    }

    [Fact]
    public async Task DirtyCpuStateWaitsForItsOwnSampleAndPublishesTheCurrentWindow()
    {
        using var run = new Run();
        run.Close(0, 1000); run.Resource.Publish(1000);
        Assert.True(await run.Owner.CollectAsync());
        var first = run.Owner.Read();
        run.Close(1000, 2000);
        var commits = 0;
        var operation = run.Owner.CollectAsync(null, (_, _) => commits++);
        Assert.False(operation.IsCompleted); Assert.Equal(0, commits);
        Assert.Same(first, run.Owner.Read());
        run.Resource.Publish(2000);
        Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, commits);
        Assert.Equal(run.Origin.AddSeconds(2), run.Owner.Read()!.MeasuredThrough);
        Assert.Equal(1000, Assert.Single(run.Owner.Read()!.Processes).ExecutionTimeMilliseconds);
    }

    [Fact]
    public async Task AcknowledgmentBudgetExpiryPublishesWarmingInsteadOfTheOldCompleteValue()
    {
        using var run = new Run();
        run.Close(0, 1000); run.Resource.Publish(1000);
        Assert.True(await run.Owner.CollectAsync()); Assert.NotNull(run.Owner.Read());
        run.Close(1000, 2000);
        var queried = true;
        Assert.True(await run.Owner.CollectAsync(null, (_, succeeded) => queried = succeeded)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(queried); Assert.Null(run.Owner.Read());
        Assert.Equal(CpuCoreResidencyUnavailableReasons.WindowWarming,
            Assert.IsType<CpuCoreResidencyUnavailable>(run.Owner.ReadObservation()).ReasonCode);
        run.Resource.Publish(2000);
        Assert.True(await run.Owner.CollectAsync()); Assert.NotNull(run.Owner.Read());
    }

    [Fact]
    public async Task CallerCancellationRetainsItsTokenAndDoesNotCommitOrStopTheRun()
    {
        using var run = new Run();
        run.Close(0, 1000);
        using var caller = new CancellationTokenSource();
        var revision = run.Owner.Revision;
        var operation = run.Owner.CollectAsync(cancellationToken: caller.Token);
        Assert.False(operation.IsCompleted);
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(revision, run.Owner.Revision);
        Assert.True(run.Resource.GetSnapshot().IsRunning);
        run.Resource.Publish(1000);
        Assert.True(await run.Owner.CollectAsync());
        Assert.NotNull(run.Owner.Read());
        Assert.Equal(1, run.Resource.Creations); Assert.Equal(0, run.Resource.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrRevokedPermitCannotCommitAnAwaitingOldRound(bool revoke)
    {
        using var run = new Run();
        run.Close(0, 1000);
        var permit = new Permit();
        var commits = 0;
        var revision = run.Owner.Revision;
        var operation = run.Owner.CollectAsync(permit, (_, _) => commits++);
        Assert.False(operation.IsCompleted);
        if (revoke) permit.Active = false; else run.Owner.PauseWindow();
        run.Resource.Publish(1000);
        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(revision, run.Owner.Revision); Assert.Equal(0, commits);
        Assert.True(run.Resource.GetSnapshot().IsRunning);
        Assert.Equal(1, run.Resource.Creations);
    }

    [Fact]
    public async Task StopDuringSampleWaitCannotCommitTheOldSession()
    {
        using var run = new Run();
        run.Close(0, 1000);
        var commits = 0;
        var revision = run.Owner.Revision;
        var operation = run.Owner.CollectAsync(null, (_, _) => commits++);
        Assert.False(operation.IsCompleted);
        run.Owner.Stop();
        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(revision, run.Owner.Revision); Assert.Equal(0, commits);
        Assert.Equal(0UL, run.Resource.RegionUsage.CurrentBytes);
    }

    [Fact]
    public async Task FatalFailureDuringPendingSampleReplacesThePreviouslyCompleteValue()
    {
        using var run = new Run();
        run.Close(0, 1000); run.Resource.Publish(1000);
        Assert.True(await run.Owner.CollectAsync()); Assert.NotNull(run.Owner.Read());
        run.Close(1000, 2000);
        var operation = run.Owner.CollectAsync();
        Assert.False(operation.IsCompleted);
        run.Resource.Fail(3);
        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(run.Owner.Read());
        Assert.Equal(CpuCoreResidencyUnavailableReasons.CaptureFailed,
            Assert.IsType<CpuCoreResidencyUnavailable>(run.Owner.ReadObservation()).ReasonCode);
    }

    private sealed class Permit : IMetricPublicationPermit
    {
        internal volatile bool Active = true;
        public bool IsActive => Active;
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool TryPublish(Action commit) { if (!Active) return false; commit(); return true; }
    }

    private sealed class Run : IDisposable
    {
        internal DateTimeOffset Origin { get; } = DateTimeOffset.UtcNow;
        internal NativeKernelEtwTestResource Resource { get; }
        internal EtwCpuResidencyCaptureOwner Owner { get; }
        internal Run()
        {
            Resource = new(NativeKernelEtwTestResource.SmallConfiguration(1000, 1), Origin);
            CpuResidencyCaptureConfiguration plan = new(1000, 32,
                new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(),
                "explicit CPU sample fixture");
            Owner = new(Resource, () => plan);
            Owner.EnsureStarted();
            Assert.False(Owner.IsWindowActive);
            // This controlled run's observation is due at QPC zero. Metadata
            // acquisition alone must not open its finite CSwitch window.
            Owner.BeginWindow(TimeSpan.FromSeconds(1));
            Assert.True(Owner.IsWindowActive);
            Resource.Publish(0);
            Resource.ProcessStart(0, Origin, 41, 11, Origin.AddDays(-1).ToFileTime(), "fixture");
            Resource.ThreadStart(0, Origin, 41, 51);
            Resource.Publish(0);
        }
        internal void Close(long from, long through)
        {
            Resource.CSwitch(from, Origin.AddMilliseconds(from), 0, 0, 0, 41, 51);
            Resource.CSwitch(through, Origin.AddMilliseconds(through), 0, 41, 51, 0, 0);
        }
        public void Dispose() { Owner.Dispose(); Resource.Dispose(); }
    }
}
