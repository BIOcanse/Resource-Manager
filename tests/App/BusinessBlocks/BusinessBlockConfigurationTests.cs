using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using static Resource_Manager_APP.Tests.BusinessBlockTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockConfigurationTests
{
    [Fact]
    public async Task EnablementDoesNotCreateDemandAndDisablePreservesExistingDemand()
    {
        var worker = new Probe();
        await using var scheduler = Create([Register(154, worker)]);
        var enabled154 = scheduler.Authority.ClaimWrite<bool>(BusinessBlockTestSupport.GateKey(154));
        try
        {
            await enabled154.PublishAsync(true, Reason(BusinessBlockRequestKind.Configuration));
            Assert.Equal(0, worker.Runs);
            Assert.Empty(scheduler.Read().Demands);
            await enabled154.PublishAsync(false, Reason(BusinessBlockRequestKind.Configuration));
            await scheduler.StartAsync(154, Reason());
            Assert.Equal(0, worker.Runs);
            Assert.Single(scheduler.Read().Demands);
            await enabled154.PublishAsync(true, Reason(BusinessBlockRequestKind.Configuration));
            await Until(() => worker.Runs == 1);
            await enabled154.PublishAsync(false, Reason(BusinessBlockRequestKind.Configuration));
            await State(scheduler, 154, BusinessBlockState.Stopped);
            Assert.Single(scheduler.Read().Demands);
            await enabled154.PublishAsync(true, Reason(BusinessBlockRequestKind.Configuration));
            await Until(() => worker.Runs == 2 && scheduler.ReadState(154) == BusinessBlockState.Running);
            Assert.Equal(1, worker.Returns);
        }
        finally { await StopAll(scheduler); }
    }

    [Fact]
    public async Task ShutdownKeepsEveryDecisionEmptyForeverWhileLaterDemandFactsRemainRecorded()
    {
        var worker = new Probe();
        await using var scheduler = Create([Register(154, worker)]);
        var enabled154 = scheduler.Authority.ClaimWrite<bool>(BusinessBlockTestSupport.GateKey(154));
        await scheduler.StartAsync(154, Reason());
        await worker.Entered.Task.WaitAsync(TestTimeout);
        await scheduler.ShutdownAsync(Reason(cause: "shutdown"));
        await State(scheduler, 154, BusinessBlockState.Stopped);
        var runs = worker.Runs;
        var shutdownSequence = scheduler.Read().Decisions.Max(item => item.Sequence);
        foreach (var kind in Enum.GetValues<BusinessBlockRequestKind>())
            await scheduler.SetDemandAsync("late:" + kind, 154, Reason(kind, "late-owner", "late-demand"));
        await enabled154.PublishAsync(false, Reason());
        await enabled154.PublishAsync(true, Reason());
        await scheduler.ShutdownAsync(Reason(cause: "repeat-shutdown"));
        var snapshot = scheduler.Read();
        Assert.True(snapshot.ShuttingDown);
        Assert.Equal(4, snapshot.Demands.Count);
        Assert.True(Assert.IsType<bool>(snapshot.Items.Single(item => item.ItemUid == BackendItems.BlockDemand && item.BlockUid == 154).Value));
        Assert.All(snapshot.Decisions.Where(item => item.Sequence > shutdownSequence), item => Assert.Empty(item.ShouldRun));
        Assert.Equal(runs, worker.Runs);
        Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(154));
    }

    [Fact]
    public async Task ShutdownAlsoOverridesCustomRules()
    {
        var worker = new Probe();
        await using var scheduler = Create([Register(154, worker)],
            [new(BackendUids.Program, [TestStateUids.Configured], input => input.Read<bool>(TestStateUids.Configured).GetValueOrDefault(false) ? [154] : [])]);
        var configuredWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Configured);
        await scheduler.ShutdownAsync(Reason());
        await configuredWriter.PublishAsync(true, Reason(BusinessBlockRequestKind.Configuration));
        Assert.Equal(0, worker.Runs);
        Assert.Empty(scheduler.Read().Decisions.Last().ShouldRun);
    }

    [Fact]
    public async Task SchedulerOwnedItemsRejectASecondOwner()
    {
        await using var scheduler = Create([Register(154, new Probe())]);
        Assert.Throws<InvalidOperationException>(() => scheduler.Authority.ClaimWrite<bool>(BackendItems.ProgramShuttingDown));
        Assert.Throws<InvalidOperationException>(() => scheduler.Authority.ClaimCell<bool>(BusinessBlockTestSupport.DemandKey(154)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create([], historyLimit: -1));
    }
}
