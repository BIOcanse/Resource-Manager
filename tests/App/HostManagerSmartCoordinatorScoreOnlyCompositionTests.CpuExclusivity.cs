using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task AutomaticExclusivityReachesOwnedCpuEffectsWithoutLockingGameAndRestoresInNormalMode()
    {
        const int gameId = 42411, backgroundId = 42412;
        const ulong birth = 133900000000000001;
        var facts = CreateCompleteProcessFacts((gameId, birth, "game", 1d, 20d, 5d),
            (backgroundId, birth + 1, "background", 100d, 20d, 5d));
        facts = facts with { Processes = [facts.Processes[0] with { SoftwareKind = SoftwareKinds.Game }, facts.Processes[1]] };
        var topology = CreateAutomaticPlacementTopology();
        var at = DateTimeOffset.UtcNow.AddSeconds(-10);
        var window = 0;
        var residency = CpuCoreResidencyTestValues.Create(91, at,
            CpuCoreResidencyTestValues.Process(gameId, birth, ("core:0", 75d)),
            CpuCoreResidencyTestValues.Process(backgroundId, birth + 1, ("core:1", 75d)));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency), cpuTopology: topology,
            cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = new Dictionary<int, IReadOnlyList<uint>> { [gameId] = [1, 2, 3, 4], [backgroundId] = [1, 2, 3, 4] };
        fixture.ProcessPolicyWriter.PlacementReadHandler = (pid, fields) =>
        {
            Assert.Equal(ProcessPlacementReadFields.DefaultCpuSets, fields);
            var process = facts.Processes.Single(item => item.ProcessId == pid);
            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new(pid,
                DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey)), process.ProcessName,
                process.ExecutablePath, null, allowed[pid], null, null));
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
        {
            Assert.NotNull(request.CpuSetIds);
            Assert.NotEmpty(request.CpuSetIds);
            Assert.Contains(fixture.StateStore.Current.AppliedPlacements, receipt => receipt.Records.Any(record => record.Metadata?.GetValueOrDefault("processId") == request.ProcessId.ToString(CultureInfo.InvariantCulture)));
            allowed[request.ProcessId] = request.CpuSetIds.ToArray();
            return new ProcessResourcePolicyBatchWriteResult(request.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(ProcessResourcePolicyBatchFields.CpuSets, true, "applied")]);
        }).ToArray();
        fixture.ProcessPolicyWriter.DefaultCpuSetWriteHandler = (pid, ids) =>
        {
            allowed[pid] = ids.ToArray();
            return new ProcessResourcePolicyWriteResult(true, "restored");
        };
        for (window = 1; window <= 8; window++)
        {
            if (window > 1) fixture.PublishNextHostedCpuObservation();
            residency = residency with { CapturedAt = at.AddSeconds(window), MeasuredFrom = at.AddSeconds(window - 5), MeasuredThrough = at.AddSeconds(window) };
            _ = await fixture.RunRealtimeCycleAsync();
            if (window == 3) Assert.Contains(1U, allowed[backgroundId]);
            if (window >= 4 && !allowed[backgroundId].Contains(1U) && allowed[gameId].Count == 4) break;
        }
        Assert.Equal([1U, 2U, 3U, 4U], allowed[gameId]);
        Assert.DoesNotContain(1U, allowed[backgroundId]);
        var avoided = Assert.Single(fixture.StateStore.Current.AppliedPlacements,
            receipt => receipt.SoftwareId == "background");
        Assert.Equal("cpu-exclusive-avoidance", Assert.Single(avoided.Records).Metadata!["source"]);
        Assert.DoesNotContain(fixture.StateStore.Current.AppliedPlacements, receipt => receipt.SoftwareId == "game");
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1, CompiledAt = DateTimeOffset.UtcNow, Reason = "exclusivity-normal-restore",
            OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
        });
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal([1U, 2U, 3U, 4U], allowed[backgroundId]);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }
}
