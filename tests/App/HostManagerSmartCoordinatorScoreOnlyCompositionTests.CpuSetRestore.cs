using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task AutomaticCpuPlacementRestoresProcessThatHadNoCpuSetsInNormalMode()
    {
        // 真机故障：几乎所有进程原本没有 CPU Sets；记录的 previous 为空，普通模式必须写空集合清除并结清所有权。
        const int processId = 42_420;
        const ulong processStartKey = 133_900_000_000_000_020;
        var startedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        var topology = CreateAutomaticPlacementTopology();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(topology);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:no-previous-cpu-sets",
            baseScore: 80,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        var residency = CpuCoreResidencyTestValues.Create(
            93,
            DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(
                processId,
                processStartKey,
                ("core:0", 50)));

        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency),
            cpuTopology: topology,
            cpuScoring: cpuScoring,
            automaticPlacementTopology: topology, singleCcdSoftwareIds: ["software:no-previous-cpu-sets"]);
        var placementScope = ReadPrivateField<HostManagerProcessEffectValidationScopeAuthority>(
            fixture.Coordinator,
            "processEffectValidationScopeAuthorityOwner");
        Assert.True(placementScope!.Capture().IsProductionUnscoped);

        IReadOnlyList<uint> currentCpuSetIds = [];
        var batchRequests = new List<ProcessResourcePolicyBatchRequest>();
        fixture.ProcessPolicyWriter.PlacementReadHandler = (actualProcessId, fields) =>
        {
            Assert.Equal(processId, actualProcessId);
            Assert.Equal(ProcessPlacementReadFields.DefaultCpuSets, fields);
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
            Assert.NotNull(request.CpuSetIds);
            batchRequests.Add(request);
            currentCpuSetIds = request.CpuSetIds.ToArray();
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [
                        new ProcessResourcePolicyBatchFieldResult(
                            ProcessResourcePolicyBatchFields.CpuSets,
                            Succeeded: true,
                            "fixture write")
                    ])
            ];
        };
        fixture.ProcessPolicyWriter.DefaultCpuSetWriteHandler = (_, _) =>
            throw new InvalidOperationException("Restoration must use the exact-process batch boundary.");

        _ = await fixture.RunRealtimeCycleAsync();

        var applied = Assert.Single(fixture.StateStore.Current.AppliedPlacements);
        var record = Assert.Single(applied.Records);
        Assert.Equal(HostManagerAppliedRecordKinds.CpuAffinity, record.Kind);
        Assert.Equal(string.Empty, record.Metadata!["previousCpuSetIds"]);
        Assert.NotEmpty(currentCpuSetIds);

        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = checked(fixture.RuntimePlan.Version + 1),
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "automatic-placement-no-previous-cpu-sets-restore",
            OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
        });
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(2, batchRequests.Count);
        Assert.Empty(batchRequests[1].CpuSetIds!);
        Assert.Empty(currentCpuSetIds);
        Assert.Equal(0, fixture.ProcessPolicyWriter.DefaultCpuSetWriteCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }
}
