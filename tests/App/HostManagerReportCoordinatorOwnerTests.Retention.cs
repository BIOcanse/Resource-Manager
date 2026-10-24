using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Fact]
    public async Task BackgroundHistoryRecordsFactsWithoutAViewerAndReleasesDemandOnStop()
    {
        var resources = new CachedResourceSource();
        var metrics = new CachedMetricSource();
        resources.Publish(CreateSoftwareMemorySnapshot(41, DateTimeOffset.UtcNow.AddMinutes(-1), 18));
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        using var owner = ScratchOwner(persistence, OperationScratchFixture.Create, resources, out _, metrics);
        using var timeout = new CancellationTokenSource(ScratchTestBound);
        await owner.StartAsync(timeout.Token);
        try
        {
            while (owner.GetStatus().LastObservedAt is null) await Task.Delay(10, timeout.Token);
            Assert.Equal(1, resources.ActiveSubscriptions);
            Assert.Equal(1, metrics.ActiveSubscriptions);
            Assert.Contains(await persistence.LoadAsync(timeout.Token),
                row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation);
        }
        finally { await owner.StopAsync(CancellationToken.None); }
        Assert.Equal(0, resources.ActiveSubscriptions);
        Assert.Equal(0, metrics.ActiveSubscriptions);
    }

    [Fact]
    public async Task SettlementDropsVanishedTargetMetadataAndKeepsNativeObservationTargets()
    {
        var resources = new CachedResourceSource();
        using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, resources, out _);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        resources.Publish(CreateSoftwareMemorySnapshot(41, DateTimeOffset.UtcNow.AddMinutes(-1), 18));
        await owner.RefreshReportsAsync(CancellationToken.None);
        var catalog = ReportOwnerField<Dictionary<ulong, HostManagerReportTargetDescriptor>>(owner, "targetCatalog")!;
        var retained = Assert.Single(catalog);
        var state = ReportOwnerField<CoordinatorState>(owner, "coordinatorState")!;
        Assert.Contains(state.CanonicalRows.Values, row => row.TargetHandle == retained.Key);
        const ulong vanished = ulong.MaxValue;
        catalog.Add(vanished, retained.Value with { TargetHandle = vanished });

        // The next settlement retains native history and removes unrelated identities.
        await owner.RefreshReportsAsync(CancellationToken.None);

        var current = Assert.Single(catalog);
        Assert.Equal(retained.Key, current.Key);
        Assert.Equal(retained.Value.SoftwareId, current.Value.SoftwareId);
        Assert.Equal(retained.Value.ProcessIds, current.Value.ProcessIds);
    }

    [Fact]
    public void FactSequencePruningFollowsRetainedObservationRows()
    {
        var retained = new NativeReportPersistenceOperation
        {
            OperationKind = (uint)NativeReportPersistenceKind.Observation,
            IdentityHandle = 101, RuleHandle = 11, TargetHandle = 12, FactSequence = 7
        };
        var removed = retained with { IdentityHandle = 102, TargetHandle = 13, FactSequence = 9 };
        var state = new CoordinatorState([retained, removed]);
        state.ApplyCommitted([removed with { Flags = (uint)NativeReportPersistenceFlags.Delete }]);
        state.PruneFactSequences();
        Assert.Equal(8UL, state.PreviewNextFactSequence(11, 12));
        Assert.Equal(1UL, state.PreviewNextFactSequence(11, 13));
    }
}
