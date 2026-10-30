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

    [Fact]
    public async Task CounterOnlyRowsWaitForTheCheckpointAndTheLastReleaseWritesThemForRestart()
    {
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        // Interval 0 is the write-every-plan reference for the same samples. Each
        // run has its own database: the second run must not import the first's rows.
        var immediate = await RunCounterSamplesAsync(observedAt, observationCheckpointIntervalMilliseconds: 0, "immediate");
        var deferred = await RunCounterSamplesAsync(observedAt, observationCheckpointIntervalMilliseconds: null, "deferred");

        Assert.Contains(immediate.HeldRows, static row => IsCounterRow(row));
        // While the run is held, cursors and growing counters wait for the checkpoint.
        Assert.DoesNotContain(deferred.HeldRows, static row => IsCounterRow(row));
        // Releasing the last hold closes the workspace and writes them first.
        Assert.Contains(deferred.ReleaseRows, static row => row.OperationKind == (uint)NativeReportPersistenceKind.Source);
        Assert.Contains(deferred.ReleaseRows, static row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation);
        // After the release both runs store the same rows with the same counters.
        // The two runs plan at different wall-clock times, so their command-time
        // fields (last complete, last observed) are left out of this comparison.
        Assert.Equal(CounterFields(immediate.Stored, commandTimes: false), CounterFields(deferred.Stored, commandTimes: false));
        // The next call imports exactly these rows and observes the same publication again.
        Assert.Equal(CounterFields(deferred.Stored), CounterFields(deferred.Restarted));
        Assert.Equal(deferred.Reports, deferred.RestartedReports);
    }

    private async Task<CounterSampleRun> RunCounterSamplesAsync(
        DateTimeOffset observedAt,
        long? observationCheckpointIntervalMilliseconds,
        string database)
    {
        var persistence = new RecordingReportStore(new NativeReportCoordinatorPersistenceStore(CreateDatabase(database)));
        var resources = new CachedResourceSource();
        await using var owner = ScratchOwner(persistence, OperationScratchFixture.Create, resources, out var provider);
        Assert.Equal(600_000, provider.Current.HostManager.ReportCoordinator.HotPublish.ObservationCheckpointIntervalMilliseconds);
        if (observationCheckpointIntervalMilliseconds is { } interval)
        {
            provider.Publish(provider.Current with
            {
                Version = 2,
                HostManager = HostManagerTestPlanFactory.CreatePlan(root =>
                    root["hot_publish"]!["report_coordinator"]!["observation_checkpoint_interval_ms"] = interval),
            });
        }

        NativeReportPersistenceOperation[] heldRows;
        resources.Publish(CreateSoftwareMemorySnapshot(41, observedAt, 5));
        await using (var call = await HoldReportCallAsync(owner))
        {
            persistence.Batches.Clear();
            for (var sample = 1; sample <= 3; sample++)
            {
                resources.Publish(CreateSoftwareMemorySnapshot(41 + sample, observedAt.AddSeconds(sample * 10), 5));
                await owner.RefreshReportsAsync(CancellationToken.None);
            }
            heldRows = persistence.Batches.SelectMany(static batch => batch).ToArray();
            persistence.Batches.Clear();
        }
        var releaseRows = persistence.Batches.SelectMany(static batch => batch).ToArray();
        var stored = await persistence.LoadAsync(CancellationToken.None);
        var reports = (await owner.GetReportsAsync(CancellationToken.None)).Reports.Select(static item => item.Id).ToArray();
        await owner.RefreshReportsAsync(CancellationToken.None);
        var restarted = await persistence.LoadAsync(CancellationToken.None);
        var restartedReports = (await owner.GetReportsAsync(CancellationToken.None)).Reports.Select(static item => item.Id).ToArray();
        return new CounterSampleRun(heldRows, releaseRows, stored, restarted, reports, restartedReports);
    }

    private static bool IsCounterRow(NativeReportPersistenceOperation row)
        => row.OperationKind is (uint)NativeReportPersistenceKind.Source
            or (uint)NativeReportPersistenceKind.Observation;

    private static string[] CounterFields(IEnumerable<NativeReportPersistenceOperation> rows, bool commandTimes = true)
        => rows.Where(IsCounterRow)
            .Select(row => string.Join(',',
                row.OperationKind, row.SourceHandle, row.RuleHandle, row.TargetHandle,
                row.SourceGeneration, row.SourceSnapshotEpoch,
                commandTimes ? row.SourceLastCompleteAtUtcMilliseconds : 0, row.SourceStatus,
                commandTimes ? row.LastObservedAtUtcMilliseconds : 0, row.CurrentValue, row.ValueSum, row.SampleDurationMilliseconds,
                row.SampleCount, row.ActiveSampleCount, row.ConsecutiveHits, row.ConsecutiveMisses, row.FactSequence))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed record CounterSampleRun(
        NativeReportPersistenceOperation[] HeldRows,
        NativeReportPersistenceOperation[] ReleaseRows,
        NativeReportPersistenceOperation[] Stored,
        NativeReportPersistenceOperation[] Restarted,
        string[] Reports,
        string[] RestartedReports);

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
