using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Fact]
    public async Task AbsentTargetRoundsStoreOnlyFrameCursorsAndRestartRestoresTheSameReport()
    {
        var persistence = new RecordingReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase()));
        var resources = new CachedResourceSource();
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        OptimizationReportItem history;
        using (var owner = ScratchOwner(persistence, OperationScratchFixture.Create, resources, out _))
        {
            for (var sample = 0; sample < 3; sample++)
            {
                resources.Publish(CreateSoftwareMemorySnapshot(41, observedAt.AddSeconds(sample * 10), 18));
                await owner.RefreshReportsAsync(CancellationToken.None);
            }
            Assert.Equal(OptimizationReportStates.Active,
                Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports).State);
            for (var round = 1; round <= 4; round++)
            {
                persistence.Batches.Clear();
                var empty = CreateSoftwareMemorySnapshot(41, observedAt.AddSeconds(20 + round * 10), 0);
                resources.Publish(empty with { Bars = [empty.Bars[0] with { Software = [] }] });
                await owner.RefreshReportsAsync(CancellationToken.None);
                var rows = persistence.Batches.SelectMany(static batch => batch).ToArray();
                // Every new frame still stores its source cursor.
                Assert.Contains(rows, static row => row.OperationKind == (uint)NativeReportPersistenceKind.Source);
                if (round == 1) continue;
                // The first absent frame stored the cleared target and report;
                // repeating the same absence stores nothing new for either.
                Assert.DoesNotContain(rows, static row => row.OperationKind == (uint)NativeReportPersistenceKind.Report
                    || (row.OperationKind == (uint)NativeReportPersistenceKind.Observation && row.RuleHandle is 1037 or 1038));
            }
            history = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
            Assert.Equal(OptimizationReportStates.History, history.State);
        }

        using var restarted = ScratchOwner(persistence, OperationScratchFixture.Create, new CachedResourceSource(), out _);
        var restored = Assert.Single((await restarted.RefreshReportsAsync(CancellationToken.None)).Reports);
        Assert.Equal(history.Id, restored.Id);
        Assert.Equal(history.State, restored.State);
        Assert.Equal(history.CreatedAt, restored.CreatedAt);
        Assert.Equal(history.FirstObservedAt, restored.FirstObservedAt);
        Assert.Equal(history.LastObservedAt, restored.LastObservedAt);
        Assert.Equal(history.Evidence, restored.Evidence);
        Assert.Equal(history.Target.SoftwareId, restored.Target.SoftwareId);
        Assert.Equal(history.Target.SoftwareName, restored.Target.SoftwareName);
    }

    private sealed class RecordingReportStore(INativeReportCoordinatorPersistenceStore inner)
        : INativeReportCoordinatorPersistenceStore
    {
        internal List<NativeReportPersistenceOperation[]> Batches { get; } = [];

        public Task<NativeReportPersistenceOperation[]> LoadAsync(CancellationToken cancellationToken)
            => inner.LoadAsync(cancellationToken);

        public Task<HostManagerReportTargetDescriptor[]> LoadTargetsAsync(CancellationToken cancellationToken)
            => inner.LoadTargetsAsync(cancellationToken);

        public async Task PersistAsync(ReadOnlyMemory<NativeReportPersistenceOperation> operations,
            CancellationToken cancellationToken, IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor>? targets = null)
        {
            await inner.PersistAsync(operations, cancellationToken, targets);
            Batches.Add(operations.ToArray());
        }
    }
}
