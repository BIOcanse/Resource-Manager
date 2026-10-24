using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCoordinatorUsesPhysicalCpuCurrentValueAndRecompilesSharedBaseline(bool removeScalarCpu)
    {
        const int pid = 4241;
        const ulong start = 132_537_599_900_000_000;
        var now = DateTimeOffset.UtcNow;
        var facts = CreateCompleteProcessFacts(pid, start, "software:physical-cpu", 100, 99, 0);
        if (removeScalarCpu)
        {
            facts = WithoutScalarCpu(facts);
        }
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 1);
        var cpu = CpuScoringPlanCompiler.Compile(topology,
            new Dictionary<int, double> { [0] = 3, [1] = 1 }, 0.5);
        CpuCoreResidencySnapshot? current = CpuCoreResidencyTestValues.Create(91, now,
            CpuCoreResidencyTestValues.Process(pid, start, ("core:1", 0), ("core:0", 40)));
        var reader = new RecordingCpuCoreResidencyReader(() => current);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, processFactsSnapshot: facts, automaticMemoryCleanupEnabled: false,
            performanceLogEnabled: true, failFastEffects: true, cpuCoreReader: reader,
            cpuTopology: topology, cpuScoring: cpu);
        // The fixture adds 100 * 0.5 CPU welfare independently of the CPU baseline ratio.
        var nativeInputs = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = nativeInputs;
        for (var index = 0; index < 3; index++)
        {
            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
            AssertWeightedProcessScore(fixture, pid, expected: 27 + 50);
            var cpuInput = Assert.Single(nativeInputs.Rows,
                row => row.ProcessId == pid && row.MetricKind == NativeSmartCoordinatorMetricKind.CpuUsagePercent);
            Assert.Equal(!removeScalarCpu, cpuInput.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.Metric));
            Assert.True(cpuInput.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore));
            Assert.Equal(removeScalarCpu ? 0 : 99, cpuInput.MetricValue);
            var counts = Assert.IsType<Dictionary<string, object?>>(
                fixture.DebugLogWriter.Records.Last().Properties["counts"]);
            Assert.Equal(1, Assert.IsType<int>(counts["scoredProcesses"]));
        }
        Assert.Equal(3, reader.ReadCount);
        Assert.Equal(99, fixture.ProcessFacts.Snapshot.Processes[0].CpuUsagePercent);

        current = CpuCoreResidencyTestValues.Create(91, now.AddSeconds(5),
            CpuCoreResidencyTestValues.Process(pid, start, ("core:1", 80)));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        AssertWeightedProcessScore(fixture, pid, expected: 18 + 50);

        var changedHost = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology,
            cpuScoring: cpu with { BaselineRatio = 0.25 });
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            HostManager = changedHost
        });
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        AssertWeightedProcessScore(fixture, pid, expected: 36 + 50);

        // Without residency the CPU domain is scored from the process's own CPU usage when the facts carry it.
        current = null;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(removeScalarCpu ? null : HostManagerCpuOccupancySource.ProcessCpuUsage,
            fixture.Coordinator.SchedulingAuthority.Compute?.CpuOccupancySource);
        AssertWeightedProcessScore(fixture, pid, expected: removeScalarCpu ? 0 : 1.8 * 99 + 50);
        var lastCounts = Assert.IsType<Dictionary<string, object?>>(
            fixture.DebugLogWriter.Records.Last().Properties["counts"]);
        Assert.Equal(removeScalarCpu ? 0 : 1, Assert.IsType<int>(lastCounts["scoredProcesses"]));
        var details = Assert.IsType<Dictionary<string, object?>>(
            fixture.DebugLogWriter.Records.Last().Properties["details"]);
        var inputCount = Assert.IsType<uint>(details["nativeInputCount"]);
        Assert.Equal(removeScalarCpu ? 1U : 2U, inputCount);
        Assert.Equal(inputCount, checked((uint)nativeInputs.Rows.Length));
        Assert.Equal(!removeScalarCpu, nativeInputs.Rows.Any(row =>
            row.MetricKind == NativeSmartCoordinatorMetricKind.CpuUsagePercent
            && row.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore)));
        Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
        Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
        Assert.Empty(reader.Subscriptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryCandidatesUsePhysicalScoresWithIndependentPublications(bool removeScalarCpu)
    {
        const int pid = 4242;
        const ulong start = 132_537_599_900_000_000;
        var facts = CreateCompleteProcessFacts(
            (pid, start, "software:physical-memory", 30, 99, 25),
            (pid + 1, start + 1, "software:physical-memory", 30, 10, 10));
        if (removeScalarCpu) facts = WithoutScalarCpu(facts);
        var physical = CpuCoreResidencyTestValues.Create(
            91, DateTimeOffset.UtcNow, CpuCoreResidencyTestValues.Process(pid, start, ("core:0", 50)));
        var reader = new RecordingCpuCoreResidencyReader(() => physical);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, processFactsSnapshot: facts, cpuCoreReader: reader,
            scoreOnlyEnabled: false, policyExecutionEnabled: true, automaticMemoryCleanupEnabled: true,
            processRecoveryRead: processId =>
            {
                Assert.Equal(pid, processId);
                return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(pid, DateTimeOffset.FromFileTime(checked((long)start))));
            });

        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(1, fixture.MemoryCleanupPlanner.PlanCalls);
        var candidate = Assert.Single(fixture.MemoryCleanupPlanner.LastRequest!.Candidates);
        Assert.Equal(pid, candidate.ProcessId);
        Assert.Equal(6.75 + 50, candidate.CpuScore, 10);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    private static SchedulingProcessFactSnapshot WithoutScalarCpu(SchedulingProcessFactSnapshot facts)
        => facts with
        {
            CurrentMetricMask = facts.CurrentMetricMask & ~SchedulingProcessMetricMask.CpuUsage,
            Processes = facts.Processes.Select(process => process with
            {
                ValidMetricMask = process.ValidMetricMask & ~SchedulingProcessMetricMask.CpuUsage
            }).ToArray(),
            DatasetObservations = facts.DatasetObservations.Where(pair => pair.Key != SchedulingProcessMetricMask.CpuUsage)
                .ToDictionary()
        };

    private static void AssertWeightedProcessScore(ScoreOnlyCoordinatorFixture fixture, int pid, double expected)
    {
        var row = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(),
            row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process && row.ProcessId == pid);
        Assert.Equal(expected, row.CpuScore, 10);
    }
}
