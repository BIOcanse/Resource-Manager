using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task AutomaticCpuPlacementUsesCanonicalScoreAppliesOnceAndRestoresInNormalMode()
    {
        const int processId = 42_410;
        const ulong processStartKey = 133_900_000_000_000_001;
        var startedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        var topology = CreateAutomaticPlacementTopology();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(topology);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:automatic-placement",
            baseScore: 80,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        var residency = CpuCoreResidencyTestValues.Create(
            91,
            DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(
                processId,
                processStartKey,
                ("core:0", 50)));

        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency),
            cpuTopology: topology,
            cpuScoring: cpuScoring,
            automaticPlacementTopology: topology);

        IReadOnlyList<uint> currentCpuSetIds = [1, 2, 3, 4];
        var boundaryEvents = new List<string>();
        fixture.ProcessPolicyWriter.PlacementReadHandler = (actualProcessId, fields) =>
        {
            Assert.Equal(processId, actualProcessId);
            Assert.Equal(ProcessPlacementReadFields.DefaultCpuSets, fields);
            boundaryEvents.Add($"read:{string.Join(',', currentCpuSetIds)}");
            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(
                new ProcessPlacementRecoverySnapshot(
                    processId,
                    startedAt,
                    $"cycle-budget-{processId}",
                    $@"c:\tests\cycle-budget-{processId}.exe",
                    null,
                    currentCpuSetIds.ToArray(),
                    null,
                    null));
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            Assert.Equal(startedAt, request.ExpectedStartedAt);
            Assert.Equal([1U, 2U], request.CpuSetIds);
            Assert.Single(fixture.StateStore.Current.AppliedPlacements);

            currentCpuSetIds = request.CpuSetIds!.ToArray();
            boundaryEvents.Add($"write:{string.Join(',', currentCpuSetIds)}");
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [
                        new ProcessResourcePolicyBatchFieldResult(
                            ProcessResourcePolicyBatchFields.CpuSets,
                            Succeeded: true,
                            "applied")
                    ])
            ];
        };
        fixture.ProcessPolicyWriter.DefaultCpuSetWriteHandler = (actualProcessId, cpuSetIds) =>
        {
            Assert.Equal(processId, actualProcessId);
            currentCpuSetIds = cpuSetIds.ToArray();
            boundaryEvents.Add($"restore:{string.Join(',', currentCpuSetIds)}");
            return new ProcessResourcePolicyWriteResult(true, "restored");
        };

        _ = await fixture.RunRealtimeCycleAsync();

        var canonicalScore = Assert.Single(
            fixture.Coordinator.SchedulingAuthority.Compute!.Cpu!.Scores,
            score => score.Kind == NativeComputeScoringOutputKind.ProcessCpu
                && score.ProcessId == processId);
        var placementWorkspace = Assert.IsType<NativePlacementCoordinatorWorkspace>(
            ReadPrivateField<NativePlacementCoordinatorWorkspace>(
                fixture.Coordinator,
                "placementCoordinatorWorkspace"));
        Assert.Equal(
            checked((uint)Math.Round(canonicalScore.Score, MidpointRounding.AwayFromZero)),
            placementWorkspace.Desired[0].Priority);
        Assert.Equal((uint)NativePlacementResourceKind.Cpu, placementWorkspace.Desired[0].ResourceKind);
        Assert.Equal((uint)NativePlacementKind.CpuSets, placementWorkspace.Desired[0].PlacementKind);
        Assert.Equal(checked((uint)processId), placementWorkspace.Desired[0].ProcessId);
        Assert.Equal(processStartKey, placementWorkspace.Desired[0].ProcessStartKey);
        Assert.Equal([1U, 2U], currentCpuSetIds);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(1, fixture.StateStore.SaveCalls);
        var applied = Assert.Single(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(OptimizationResourceKinds.Cpu, applied.ResourceKind);
        var record = Assert.Single(applied.Records);
        Assert.Equal(HostManagerAppliedRecordKinds.CpuAffinity, record.Kind);
        Assert.Equal(processStartKey.ToString(), record.Metadata!["processStartKey"]);
        Assert.True(boundaryEvents.IndexOf("read:1,2,3,4") < boundaryEvents.IndexOf("write:1,2"));

        var savesAfterApply = fixture.StateStore.SaveCalls;
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(savesAfterApply, fixture.StateStore.SaveCalls);
        Assert.Equal([1U, 2U], currentCpuSetIds);

        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = checked(fixture.RuntimePlan.Version + 1),
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "automatic-placement-normal-restore",
            OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
        });
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([1U, 2U, 3U, 4U], currentCpuSetIds);
        Assert.Equal(
            (uint)NativePlacementActionDisposition.Restore,
            placementWorkspace.Actions[0].Disposition);
        Assert.NotEqual(0UL, placementWorkspace.Actions[0].DesiredDigest);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(1, fixture.ProcessPolicyWriter.DefaultCpuSetWriteCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(1, fixture.ProcessPolicyWriter.DefaultCpuSetWriteCalls);
        Assert.Contains("restore:1,2,3,4", boundaryEvents);
    }

    private static CpuTopologySnapshot CreateAutomaticPlacementTopology()
    {
        var physicalCores = Enumerable.Range(0, 4)
            .Select(index => new CpuPhysicalCoreModel(
                $"core:{index}",
                index,
                $"Core {index}",
                index < 2 ? "ccd:0" : "ccd:1",
                0,
                1,
                null,
                [index],
                []))
            .ToArray();
        var logicalProcessors = Enumerable.Range(0, 4)
            .Select(index => new CpuLogicalProcessorModel(
                index,
                0,
                index,
                $"core:{index}",
                index < 2 ? "ccd:0" : "ccd:1",
                1,
                null,
                true,
                checked((uint)(index + 1))))
            .ToArray();
        return new CpuTopologySnapshot(
            DateTimeOffset.UtcNow,
            "Automatic placement CPU",
            new CpuSpecificationModel(
                "Automatic placement CPU",
                "Test",
                "Test",
                4,
                4,
                null,
                null,
                null,
                null,
                "fixture"),
            "fixture",
            "fixture",
            CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
            CpuTopologyVisualLayoutKinds.CcdGrid,
            "fixture",
            4,
            4,
            2,
            false,
            [
                new CpuCcdModel("ccd:0", 0, "CCD 0", null, [0, 1], [0, 1], "fixture"),
                new CpuCcdModel("ccd:1", 1, "CCD 1", null, [2, 3], [2, 3], "fixture")
            ],
            physicalCores,
            logicalProcessors,
            []);
    }
}
