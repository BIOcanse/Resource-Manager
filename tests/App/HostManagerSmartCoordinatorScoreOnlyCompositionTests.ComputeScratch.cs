using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task ActualCoordinatorUsesTheOwnedScratchAndRebuildsItAfterIdleEviction()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, automaticMemoryCleanupEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(4242, 100, "software:scratch", 20, 50, 25));
        var ledger = Assert.IsType<ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner>(
            fixture.OwnedComputeLedger);
        var journalScratch = JournalWorkspaceScratch(fixture);
        Assert.True(journalScratch.Length >= 4096);
        Assert.Equal(journalScratch.Length, ledger.ReadTable().RegisteredBytes);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var first = Assert.IsType<ResourceManager.App.Infrastructure.Optimization.HostManagerComputeScoringCycleResult>(
            fixture.Coordinator.SchedulingAuthority.Compute);
        var saved = first.Cpu!.Scores.Select(value => value.Score).ToArray();
        var registered = ledger.ReadTable().RegisteredBytes;
        Assert.True(registered > 4096);
        var smartScratch = Assert.IsAssignableFrom<ResourceManager.App.Infrastructure.Adaptation.IComputeScratch>(
            ReadPrivateReference(fixture.Workspace, "scratch"));
        var scoringScratch = Assert.IsAssignableFrom<ResourceManager.App.Infrastructure.Adaptation.IComputeScratch>(
            ReadPrivateReference(ReadPrivateReference(fixture.Coordinator, "computeScoringWorkspace")!, "scratch"));
        var ownership = Assert.IsType<ResourceManager.App.Infrastructure.NativeCore.NativeAppliedOwnershipOwner>(
            ReadPrivateReference(fixture.NativeTransactions.AppliedOwnership, "owner"));
        var ownershipScratch = Assert.IsAssignableFrom<ResourceManager.App.Infrastructure.Adaptation.IComputeScratch>(
            ReadPrivateReference(ReadPrivateReference(ownership, "workspace")!, "scratch"));
        var ownershipBytes = checked((ulong)ownership.Capacity.RecordCapacity *
            ResourceManager.App.Infrastructure.NativeCore.NativeAppliedOwnershipAbi.RecordSize + ownership.Capacity.MaximumImageBytes);
        Assert.Equal(ownershipBytes, ownershipScratch.Length);
        Assert.Equal(scoringScratch.Length + smartScratch.Length + ownershipBytes + journalScratch.Length, registered);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(4UL, ledger.Tick().Released); // Scoring, Smart, ownership and journal are separate real resources.
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(registered - journalScratch.Length, ledger.ReadTable().RegisteredBytes);
        // Scoring does not use the journal. Its workspace returns only on a real journal read.
        await fixture.ReadJournalSnapshotAsync();
        Assert.Equal(registered, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(saved, fixture.Coordinator.SchedulingAuthority.Compute!.Cpu!.Scores.Select(value => value.Score));
        Assert.Equal(saved, first.Cpu.Scores.Select(value => value.Score));

        // Recompilation follows the real construct-new/dispose-old production route.
        var oldWorkspace = ReadPrivateReference(fixture.Coordinator, "computeScoringWorkspace");
        var cpu = fixture.RuntimePlan.HostManager.CpuScoring!;
        var changed = HostManagerTestPlanFactory.CreatePlan(cpuScoring: cpu with { BaselineRatio = cpu.BaselineRatio / 2 });
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            HostManager = changed
        });
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotSame(oldWorkspace, ReadPrivateReference(fixture.Coordinator, "computeScoringWorkspace"));
        Assert.NotNull(fixture.Coordinator.SchedulingAuthority.Compute?.Cpu);
        Assert.Equal(registered, ledger.ReadTable().RegisteredBytes); // Scoring replacement preserves Smart and ownership identities.
        await fixture.Coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(ResourceManager.App.Infrastructure.NativeCore.NativeAppliedOwnershipOwnerState.Ready, ownership.State);
        await fixture.NativeTransactions.DisposeAsync();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }
    private static ResourceManager.App.Infrastructure.Adaptation.IComputeScratch JournalWorkspaceScratch(
        ScoreOnlyCoordinatorFixture fixture)
    {
        var runtime = Assert.IsType<ResourceManager.App.Infrastructure.Optimization.Transactions.HostManagerTransactionJournalRuntime>(
            ReadPrivateReference(fixture.NativeTransactions, "journal"));
        var owner = Assert.IsType<ResourceManager.App.Infrastructure.NativeCore.NativeTransactionJournalOwner>(
            ReadPrivateReference(runtime, "journalOwner"));
        return Assert.IsAssignableFrom<ResourceManager.App.Infrastructure.Adaptation.IComputeScratch>(
            ReadPrivateReference(ReadPrivateReference(owner, "workspace")!, "scratch"));
    }

}
