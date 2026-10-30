using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task GpuRoutePreflightStaysIdleAcrossCalmCyclesAndReadsAgainWhenAMoveAppears()
    {
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1,
            DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 789,
            [new(0, "NVIDIA GeForce RTX 3060", 0x10de, 1, 0, new AdapterLuid { LowPart = 123 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated),
             new(1, "NVIDIA GeForce RTX 4090", 0x10de, 2, 0, new AdapterLuid { LowPart = 456 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)]);
        var process = AutomaticGpuProcess();
        var identity = new GpuPlacementProcessInstance(process.ProcessId, process.ProcessStartKey, process.ProcessName, process.ExecutablePath!);
        // On the strongest adapter without overflow the plan keeps the process where it is.
        var calm = CreateNativeGpuCycleFacts(identity, process.SoftwareId, inventory, 1, includeDedicatedMemory: true);
        var actions = new RecordingRunningGpuActions { Prepare = static _ => new(null, "fixture route rejected") };
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: calm, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology, runningGpuActions: actions,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(pid, DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey)))));
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, 456, includeDedicatedMemory: true));
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                { [process.SoftwareId] = process.Policy }, new Dictionary<string, ResolvedGpuPlacementPolicy>())
        });
        for (var cycle = 0; cycle < 3; cycle++) await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal(0, actions.PrepareCalls);
        Assert.Equal(0, actions.PreparationCycles);

        // On the weaker adapter the plan wants a move, so the live route is read in that cycle.
        fixture.ProcessFacts.SetSnapshot(CreateNativeGpuCycleFacts(identity, process.SoftwareId, inventory, 0, includeDedicatedMemory: true));
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, 123, includeDedicatedMemory: true));
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal(1, actions.PrepareCalls);
        Assert.Equal(1, actions.PreparationCycles);

        fixture.ProcessFacts.SetSnapshot(calm);
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, 456, includeDedicatedMemory: true));
        for (var cycle = 0; cycle < 2; cycle++) await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal(1, actions.PrepareCalls);
        Assert.Equal(1, actions.PreparationCycles);
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }
}
