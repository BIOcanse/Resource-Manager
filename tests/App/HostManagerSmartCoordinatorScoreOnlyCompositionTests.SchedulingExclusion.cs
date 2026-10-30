using System.Reflection;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

// Each software-level exclusion removes exactly its own domain (CPU, memory or GPU) and is read
// from the published runtime plan; the other two domains must stay identical.
public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task CpuSchedulingExclusionAloneClearsAutomaticProcessPolicyEligibility(
        bool cpuExcluded,
        bool memoryExcluded,
        bool gpuExcluded)
    {
        const string software = "software:exclusion-process-policy";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            optimizationMode: AppOptimizationModes.Smart,
            processFactsSnapshot: CreateCompleteProcessFacts(4_252, 132_537_599_900_000_200, software, 60, 10, 20));
        var baseline = await CaptureIdentityRowsAsync(fixture);
        Assert.All(baseline, static row =>
            Assert.True(row.Flags.HasFlag(NativeSmartCoordinatorInputFlags.CanApplyProcessPolicy)));

        PublishSchedulingExclusions(fixture, software, cpuExcluded, memoryExcluded, gpuExcluded);
        var excluded = await CaptureIdentityRowsAsync(fixture);

        Assert.Equal(baseline.Length, excluded.Length);
        for (var index = 0; index < baseline.Length; index++)
        {
            var expectedFlags = cpuExcluded
                ? baseline[index].Flags & ~NativeSmartCoordinatorInputFlags.CanApplyProcessPolicy
                : baseline[index].Flags;
            Assert.Equal(expectedFlags, excluded[index].Flags);
            // Protection keeps its own meaning ("never lowered") and is not touched by an exclusion.
            Assert.Equal(baseline[index].ProtectionLevel, excluded[index].ProtectionLevel);
        }
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, false)]
    public async Task MemoryPriorityProjectionHonorsOnlyMemorySchedulingExclusion(
        bool cpuExcluded,
        bool memoryExcluded,
        bool gpuExcluded,
        bool expectedDirective)
    {
        const int pid = 24_061;
        const ulong startKey = 132_537_599_910_000_100;
        const string software = "software:memory-exclusion";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, software, 20, 50, 25),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            failFastEffects: true);
        PublishSchedulingExclusions(fixture, software, cpuExcluded, memoryExcluded, gpuExcluded);

        _ = await fixture.RunRealtimeCycleAsync();

        var projection = Assert.IsType<HostManagerNonAdaptedMemoryModeProjectionSnapshot>(
            fixture.Coordinator.NonAdaptedMemoryModeProjection);
        Assert.Equal(expectedDirective ? 1 : 0, projection.Processes.Length);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task MemorySchedulingExclusionRestoresOwnedMemoryPriorityAndRejoiningAppliesAgain()
    {
        const int pid = 24_062;
        var startedAt = CreateProcessStartedAtOrderedBeforeSoftwareTarget(pid);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var software = CreateSoftwareIdOrderedAfterProcessTarget(pid, startKey);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, software, 20, 50, 25),
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(pid, startedAt)),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        var currentPriority = 5U;
        var writes = new List<uint>();
        fixture.ProcessPolicyWriter.ProcessReadHandler = _ => new ProcessResourcePolicySnapshot(
            pid, "excluded-memory", @"c:\tests\excluded-memory.exe", startedAt,
            "Normal", ProcessorAffinityMask: 1, LogicalProcessorCount: 1,
            MemoryPriority: currentPriority);
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(pid, request.ProcessId);
            currentPriority = Assert.IsType<uint>(request.MemoryPriority);
            writes.Add(currentPriority);
            return [new ProcessResourcePolicyBatchWriteResult(pid,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.MemoryPriority, true, "applied")])];
        };

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal([3U], writes);

        PublishSchedulingExclusions(fixture, software, false, true, false);
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([3U, 5U], writes);
        Assert.Empty(fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);

        PublishSchedulingExclusions(fixture, software, false, false, false);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal([3U, 5U, 3U], writes);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task AutomaticMemoryCleanupHonorsOnlyMemorySchedulingExclusion(
        bool cpuExcluded,
        bool memoryExcluded,
        bool gpuExcluded)
    {
        const int processId = 24_224;
        const string softwareId = "software:final-exclusion";
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                softwareId,
                baseScore: 20,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(processId, startedAt)),
            automaticMemoryCleanupEnabled: true,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        fixture.MemoryCleanupPlanner.PlanHandler = CreateFinalAdmissionAwareMemoryCleanupPlan;
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests
            .Select(static request => new ProcessResourcePolicyBatchWriteResult(
                request.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.TrimWorkingSet, Succeeded: true, "trimmed")]))
            .ToArray();
        // Published before priming: a new plan version restarts the hosted cleanup admission, and the
        // cycle's publication lease keeps any later publication out of the round under test.
        PublishSchedulingExclusions(fixture, softwareId, cpuExcluded, memoryExcluded, gpuExcluded);
        await fixture.PrimeMemoryCleanupAdmissionAsync();

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.True(
            fixture.MemoryCleanupPlanner.PlanCalls == 1,
            fixture.DescribeMemoryCleanupState());
        var candidate = Assert.Single(fixture.MemoryCleanupPlanner.LastRequest!.Candidates);
        Assert.Equal(processId, candidate.ProcessId);
        Assert.Equal(!memoryExcluded, candidate.CanApply);
        Assert.Equal(memoryExcluded ? 0 : 1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(memoryExcluded ? 0 : 1, fixture.MemoryCleanupPlanner.CompleteCalls);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    public async Task CpuSchedulingExclusionRestoresOwnedCpuPlacementWhileOtherExclusionsKeepIt(
        bool cpuExcluded,
        bool memoryExcluded,
        bool gpuExcluded,
        bool expectRestore)
    {
        const int processId = 42_431;
        const ulong processStartKey = 133_900_000_000_000_041;
        const string software = "software:cpu-exclusion-placement";
        var startedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        var topology = CreateAutomaticPlacementTopology();
        var residency = CpuCoreResidencyTestValues.Create(94, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(processId, processStartKey, ("core:0", 50)));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(processId, processStartKey, software, 80, 50, 25),
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency),
            cpuTopology: topology,
            cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology,
            singleCcdSoftwareIds: [software]);
        IReadOnlyList<uint> currentCpuSetIds = [];
        var batchRequests = new List<ProcessResourcePolicyBatchRequest>();
        fixture.ProcessPolicyWriter.PlacementReadHandler = (actualProcessId, _) =>
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new ProcessPlacementRecoverySnapshot(
                actualProcessId, startedAt, "cpu-exclusion", @"c:\tests\cpu-exclusion.exe",
                null, currentCpuSetIds.ToArray(), null, null));
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.NotNull(request.CpuSetIds);
            batchRequests.Add(request);
            currentCpuSetIds = request.CpuSetIds.ToArray();
            return [new ProcessResourcePolicyBatchWriteResult(processId,
                [new ProcessResourcePolicyBatchFieldResult(ProcessResourcePolicyBatchFields.CpuSets, true, "fixture write")])];
        };

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Single(fixture.StateStore.Current.AppliedPlacements);
        Assert.NotEmpty(currentCpuSetIds);

        PublishSchedulingExclusions(fixture, software, cpuExcluded, memoryExcluded, gpuExcluded);
        _ = await fixture.RunRealtimeCycleAsync();

        if (expectRestore)
        {
            Assert.Equal(2, batchRequests.Count);
            Assert.Empty(batchRequests[1].CpuSetIds!);
            Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        }
        else
        {
            Assert.Single(batchRequests);
            Assert.Single(fixture.StateStore.Current.AppliedPlacements);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CpuSchedulingExclusionNeverReadsOrHintsThreadsAndRestoresOwnedHints(bool excludedFromStart)
    {
        const int pid = 42_432;
        const ulong startKey = 133_900_000_000_000_042;
        const string software = "software:cpu-exclusion-thread-hints";
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, software, 80, 50, 25),
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.CpuOnly,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => CpuCoreResidencyTestValues.Create(
                96, DateTimeOffset.UtcNow, CpuCoreResidencyTestValues.Process(pid, startKey, ("core:0", 50)))),
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology);
        var threads = new Dictionary<int, ThreadIdealProcessorPolicySnapshot>
        {
            [10001] = new(pid, startKey, 10001, startKey + 1, new(0, 0), 0, 15, []),
            [10002] = new(pid, startKey, 10002, startKey + 2, new(0, 0), 0, 15, [])
        };
        var excluded = excludedFromStart;
        fixture.ProcessPolicyWriter.IdealThreadsReadHandler = (_, _, _) => excluded
            ? throw new InvalidOperationException("A CPU-excluded process must not be inspected for thread hints.")
            : RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Found(threads.Values.ToArray());
        fixture.ProcessPolicyWriter.PlacementReadHandler = (_, _) =>
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new(pid, DateTimeOffset.FromFileTime((long)startKey),
                "fixture", @"C:\tests\fixture.exe", null, [1, 2, 3, 4], null, null));
        fixture.ProcessPolicyWriter.IdealReadHandler = (_, _, threadId) =>
            RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(threads[threadId]);
        fixture.ProcessPolicyWriter.IdealWriteHandler = (expected, desired) =>
        {
            threads[expected.ThreadId] = expected with { IdealProcessor = desired };
            return new(true, "recorded hint only");
        };
        if (excludedFromStart)
        {
            PublishSchedulingExclusions(fixture, software, true, false, false);
            _ = await fixture.RunRealtimeCycleAsync();

            Assert.Equal(0, fixture.ProcessPolicyWriter.IdealProcessorWriteCalls);
            Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
            return;
        }

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(1, fixture.ProcessPolicyWriter.IdealProcessorWriteCalls);
        Assert.Single(fixture.StateStore.Current.AppliedPlacements);

        excluded = true;
        PublishSchedulingExclusions(fixture, software, true, false, false);
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(2, fixture.ProcessPolicyWriter.IdealProcessorWriteCalls);
        Assert.All(threads.Values, static thread => Assert.Equal(new ThreadIdealProcessor(0, 0), thread.IdealProcessor));
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    private static async Task<NativeSmartCoordinatorInputRow[]> CaptureIdentityRowsAsync(
        ScoreOnlyCoordinatorFixture fixture)
    {
        var capture = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = capture;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var rows = capture.Rows
            .Where(static row => row.MetricKind == NativeSmartCoordinatorMetricKind.None
                && row.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessIdentity))
            .OrderBy(static row => row.ProcessId)
            .ToArray();
        Assert.NotEmpty(rows);
        return rows;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GpuSchedulingExclusionLeavesAnExecutedRuntimeSwitchToTheExistingRestoreLifecycle(
        bool gpuExcluded,
        bool expectHeld)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions);
        var process = AutomaticGpuProcess();
        var desired = (await CreateGpuDesired(fixture.Coordinator, process, null))!;
        await ApplyGpuDesired(fixture, desired);
        var executed = Assert.Single(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(HostManagerAppliedRecordKinds.GpuShimPolicy, Assert.Single(executed.Records).Kind);
        var next = process with
        {
            CanonicalGpuScores = new Dictionary<ulong, double>(),
            Policy = process.Policy with { GpuSchedulingExcluded = gpuExcluded }
        };

        // Without a GPU score a participating process keeps its switch until the score returns. An excluded
        // process wants nothing, so the switch leaves the desired set and the existing receipt owner restores it.
        var wanted = await CreateAutomaticPlacementDesiredAsync(fixture, [next], [executed]);

        Assert.Equal(expectHeld, wanted.Any(item => item.Record.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy));
        if (gpuExcluded)
        {
            Assert.Null(await CreateGpuDesired(fixture.Coordinator, next, executed));
        }
        Assert.Equal(1, actions.PrepareCalls);
        Assert.Equal(1, actions.ApplyCalls);
    }

    private static Task<IReadOnlyList<HostManagerPlacementDesired>> CreateAutomaticPlacementDesiredAsync(
        ScoreOnlyCoordinatorFixture fixture,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> existing)
        => (Task<IReadOnlyList<HostManagerPlacementDesired>>)typeof(HostManagerSmartCoordinator)
            .GetMethod("CreateAutomaticPlacementDesiredAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator,
            [
                fixture.ProcessPolicyWriter.CreateThreadIdealProcessorReadCycle(),
                null, null, processes, new HostManagerAutomaticPlacementPlan([], []), existing,
                CancellationToken.None, false, true, null, true, null, null, 0
            ])!;

    // Publishes the exclusions through the runtime plan, the same path the policy PUT takes after
    // RuntimeSpecializationCoordinator.RebuildAsync; every other field of the resolved policy is kept.
    private static void PublishSchedulingExclusions(
        ScoreOnlyCoordinatorFixture fixture,
        string softwareId,
        bool cpuExcluded,
        bool memoryExcluded,
        bool gpuExcluded)
    {
        var current = fixture.RuntimePlanProvider.Current;
        var policies = new Dictionary<string, ResolvedGpuPlacementPolicy>(
            current.GpuPlacement.SoftwarePoliciesBySoftwareId,
            StringComparer.OrdinalIgnoreCase)
        {
            [softwareId] = current.GpuPlacement.Resolve(softwareId, softwareId, null, null) with
            {
                CpuSchedulingExcluded = cpuExcluded,
                MemorySchedulingExcluded = memoryExcluded,
                GpuSchedulingExcluded = gpuExcluded
            }
        };
        fixture.RuntimePlanProvider.Publish(current with
        {
            Version = checked(current.Version + 1),
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "scheduling-exclusion-test",
            GpuPlacement = current.GpuPlacement with { SoftwarePoliciesBySoftwareId = policies }
        });
    }
}
