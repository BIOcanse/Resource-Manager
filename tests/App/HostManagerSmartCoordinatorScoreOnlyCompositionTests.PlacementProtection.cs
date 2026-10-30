using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task SoftwareLevel2ProtectionExcludesAutomaticPhysicalPlacement()
    {
        const int pid = 42_410;
        const ulong startKey = 133_900_000_000_000_001;
        const string software = "software:automatic-placement";
        var topology = CreateAutomaticPlacementTopology();
        var residency = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(pid, startKey, ("core:0", 50)));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, software, 80, 50, 25),
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency), cpuTopology: topology,
            cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        fixture.ProtectionService.Targets = [CreateMemoryProtection(software, OptimizationProtectionStates.Active, 2)];
        fixture.ProcessPolicyWriter.PlacementReadHandler = (_, _) => throw new InvalidOperationException("Protected software must not be placed.");
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }
}
