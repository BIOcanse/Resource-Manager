using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private static SchedulingProcessFactSnapshot CreateRoundProcessFacts()
        => CreateCompleteProcessFacts(4_242, 132_537_599_900_000_000,
            "software:round", baseScore: 20, cpuUsagePercent: 10, memoryUsagePercent: 5);

    [Fact]
    public async Task UnchangedPublicationsReuseRoundAndRecomputeTheSameScores()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, processFactsSnapshot: CreateRoundProcessFacts(), memoryModePolicyEnabled: true);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var first = fixture.Coordinator.SchedulingAuthority;
        Assert.True(first.Compute is not null, first.UnavailableReason);
        var firstCompute = Assert.IsType<HostManagerComputeScoringCycleResult>(first.Compute);
        var firstNative = fixture.Workspace.CurrentSnapshotRows.ToArray();
        var firstCycle = fixture.Workspace.Snapshot.CycleSequence;

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var second = fixture.Coordinator.SchedulingAuthority;
        var secondCompute = Assert.IsType<HostManagerComputeScoringCycleResult>(second.Compute);
        Assert.Equal(1UL, first.RoundNumber);
        Assert.Equal(first.RoundNumber, second.RoundNumber);
        Assert.NotSame(firstCompute, secondCompute);
        Assert.Equal(firstCompute.Cpu!.Scores.ToArray(), secondCompute.Cpu!.Scores.ToArray());
        Assert.Equal(firstCompute.Gpu?.Scores.ToArray(), secondCompute.Gpu?.Scores.ToArray());
        Assert.Equal(first.MemoryModes!.Software.ToArray(), second.MemoryModes!.Software.ToArray());
        Assert.True(fixture.Workspace.Snapshot.CycleSequence > firstCycle);
        Assert.Equal(firstNative.Select(row => row.ProcessPendingCount),
            fixture.Workspace.CurrentSnapshotRows.ToArray()
                .Select(row => row.ProcessPendingCount));
    }

    [Fact]
    public async Task SameValueHardwarePublicationStartsRoundWithoutContentGenerationChange()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsSnapshot: CreateRoundProcessFacts(),
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var firstRound = fixture.Coordinator.SchedulingAuthority.RoundNumber;
        var before = fixture.MetricSampler.Snapshot;
        var datasets = before.Datasets.ToDictionary(pair => pair.Key, pair => pair.Value);
        var memory = datasets[ResourceManager.App.Domain.Monitoring.SamplingDatasetIds.SystemMemoryUsage];
        datasets[memory.DatasetId] = memory with { SourceGeneration = memory.SourceGeneration + 1 };
        fixture.MetricSampler.SetSnapshot(before with { Datasets = datasets });
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(before.CommittedGeneration, fixture.MetricSampler.Snapshot.CommittedGeneration);
        Assert.Equal(firstRound + 1, fixture.Coordinator.SchedulingAuthority.RoundNumber);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(firstRound + 1, fixture.Coordinator.SchedulingAuthority.RoundNumber);
    }

    [Fact]
    public async Task IndependentProcessPublicationAdvancesRoundWithoutInventoryChange()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsSnapshot: CreateRoundProcessFacts(),
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var inventoryGeneration = fixture.ProcessFacts.Snapshot.Generation;
        var firstRound = fixture.Coordinator.SchedulingAuthority.RoundNumber;
        fixture.ProcessFacts.PublishNextHostedCpuObservation();
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(inventoryGeneration, fixture.ProcessFacts.Snapshot.Generation);
        Assert.Equal(firstRound + 1, fixture.Coordinator.SchedulingAuthority.RoundNumber);
    }

    [Fact]
    public async Task ResidencyPublicationSwitchesFromProcessCpuAndClockRollbackStartsANewRound()
    {
        CpuCoreResidencySnapshot? residency = null;
        var reader = new RecordingCpuCoreResidencyReader(() => residency);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsSnapshot: CreateRoundProcessFacts(),
            memoryModePolicyEnabled: true, cpuCoreReader: reader);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage,
            fixture.Coordinator.SchedulingAuthority.Compute!.CpuOccupancySource);
        var firstRound = fixture.Coordinator.SchedulingAuthority.RoundNumber;
        var facts = fixture.ProcessFacts.Snapshot;
        residency = CpuCoreResidencyTestValues.Create(1, DateTimeOffset.UtcNow,
            facts.Processes.Select(process => CpuCoreResidencyTestValues.Process(
                process.ProcessId, process.ProcessStartKey, ("core:0", process.CpuUsagePercent))).ToArray());
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(firstRound + 1, fixture.Coordinator.SchedulingAuthority.RoundNumber);
        Assert.Equal(HostManagerCpuOccupancySource.CoreResidency,
            fixture.Coordinator.SchedulingAuthority.Compute!.CpuOccupancySource);
        var round = fixture.Coordinator.SchedulingAuthority.RoundNumber;
        residency = residency with { CapturedAt = residency.CapturedAt.AddMinutes(-1),
            MeasuredFrom = residency.MeasuredFrom.AddMinutes(-1),
            MeasuredThrough = residency.MeasuredThrough.AddMinutes(-1) };
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(round + 1, fixture.Coordinator.SchedulingAuthority.RoundNumber);
        Assert.NotNull(fixture.Coordinator.SchedulingAuthority.Compute);
    }

    [Fact]
    public async Task CaptureFailurePublishesCurrentRoundAndStartsAtZero()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsSnapshot: CreateRoundProcessFacts());
        fixture.MetricSampler.ObservationAvailable = false;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0UL, fixture.Coordinator.SchedulingAuthority.RoundNumber);
        Assert.Equal(HostManagerSchedulingAuthorityAvailability.Unavailable,
            fixture.Coordinator.SchedulingAuthority.Availability);
        fixture.MetricSampler.ObservationAvailable = true;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var firstRound = fixture.Coordinator.SchedulingAuthority.RoundNumber;
        Assert.Equal(1UL, firstRound);
        fixture.MetricSampler.ObservationAvailable = false;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(firstRound, fixture.Coordinator.SchedulingAuthority.RoundNumber);
        fixture.MetricSampler.ObservationAvailable = true;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(firstRound, fixture.Coordinator.SchedulingAuthority.RoundNumber);
    }

    [Fact]
    public async Task DecisionSnapshotShowsOnlyCapturedRoundForComputedResults()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsSnapshot: CreateRoundProcessFacts(),
            memoryModePolicyEnabled: true);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var snapshot = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        var authority = snapshot.SchedulingAuthority;
        Assert.Equal("1", authority.RoundNumber);
        Assert.Equal("ready", authority.Availability);
        Assert.All(authority.Cpu!.Scores, score =>
            Assert.Contains(score.Kind, new[] { "process_cpu", "software_cpu" }));
        Assert.All(authority.MemoryModes!.Software, row =>
            Assert.Contains(row.Mode, new[] { "unrestricted", "normal", "optimize", "paged_frozen" }));
        Assert.All(snapshot.NonAdaptedMemoryDecisions, row =>
            Assert.Contains(row.Action, new[] { "restore_owned_constraints", "optimize", "paged_frozen" }));
        Assert.Equal(authority.RoundNumber, authority.Cpu!.RoundNumber);
        Assert.All(authority.Cpu.Scores, score => Assert.Equal(authority.RoundNumber, score.RoundNumber));
        Assert.Equal(authority.RoundNumber, authority.MemoryModes!.RoundNumber);
        Assert.All(authority.MemoryModes.Software, row => Assert.Equal(authority.RoundNumber, row.RoundNumber));
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"roundNumber\":\"1\"", json);
        Assert.DoesNotContain("sourceGeneration", json);
        Assert.DoesNotContain("snapshotGeneration", json);
    }
}
