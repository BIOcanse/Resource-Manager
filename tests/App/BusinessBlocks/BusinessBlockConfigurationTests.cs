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
        try
        {
            await scheduler.SetEnabledAsync(154, true, Reason(BusinessBlockRequestKind.Configuration));
            Assert.Equal(0, worker.Runs);
            Assert.Empty(scheduler.Read().Demands);
            await scheduler.SetEnabledAsync(154, false, Reason(BusinessBlockRequestKind.Configuration));
            await scheduler.StartAsync(154, Reason());
            Assert.Equal(0, worker.Runs);
            Assert.Single(scheduler.Read().Demands);
            await scheduler.SetEnabledAsync(154, true, Reason(BusinessBlockRequestKind.Configuration));
            await Until(() => worker.Runs == 1);
            await scheduler.SetEnabledAsync(154, false, Reason(BusinessBlockRequestKind.Configuration));
            await State(scheduler, 154, BusinessBlockState.Stopped);
            Assert.Single(scheduler.Read().Demands);
            await scheduler.SetEnabledAsync(154, true, Reason(BusinessBlockRequestKind.Configuration));
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
        await scheduler.StartAsync(154, Reason());
        await worker.Entered.Task.WaitAsync(TestTimeout);
        await scheduler.ShutdownAsync(Reason(cause: "shutdown"));
        await State(scheduler, 154, BusinessBlockState.Stopped);
        var runs = worker.Runs;
        var shutdownSequence = scheduler.Read().Decisions.Max(item => item.Sequence);
        foreach (var kind in Enum.GetValues<BusinessBlockRequestKind>())
            await scheduler.SetDemandAsync("late:" + kind, 154, Reason(kind, "late-owner", "late-demand"));
        await scheduler.SetEnabledAsync(154, false, Reason());
        await scheduler.SetEnabledAsync(154, true, Reason());
        await scheduler.ShutdownAsync(Reason(cause: "repeat-shutdown"));
        var snapshot = scheduler.Read();
        Assert.True(snapshot.ShuttingDown);
        Assert.Equal(4, snapshot.Demands.Count);
        Assert.True(snapshot.Facts.Single(item => item.Key == BusinessBlockScheduler.DemandFact(154)).Value);
        Assert.All(snapshot.Decisions.Where(item => item.Sequence > shutdownSequence), item => Assert.Empty(item.ShouldRun));
        Assert.Equal(runs, worker.Runs);
        Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(154));
    }

    [Fact]
    public async Task ShutdownAlsoOverridesCustomRules()
    {
        var worker = new Probe();
        await using var scheduler = Create([Register(154, worker)],
            [new(BackendUids.Program, ["configured"], input => input.Facts["configured"].Value ? [154] : [])]);
        await scheduler.ShutdownAsync(Reason());
        await scheduler.SetFactAsync("configured", true, Reason(BusinessBlockRequestKind.Configuration));
        Assert.Equal(0, worker.Runs);
        Assert.Empty(scheduler.Read().Decisions.Last().ShouldRun);
    }

    [Fact]
    public async Task ReservedFactsRequireTheirOwningPublicOperation()
    {
        await using var scheduler = Create([Register(154, new Probe())]);
        foreach (var key in new[] { BusinessBlockScheduler.ShutdownFact, BusinessBlockScheduler.DemandFact(154), BusinessBlockScheduler.EnabledFact(154) })
            Assert.Throws<ArgumentException>(() => { _ = scheduler.SetFactAsync(key, true, Reason()); });
        Assert.Throws<ArgumentOutOfRangeException>(() => Create([], historyLimit: -1));
    }
}
