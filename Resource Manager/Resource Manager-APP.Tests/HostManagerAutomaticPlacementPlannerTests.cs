using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerAutomaticPlacementPlannerTests
{
    [Fact]
    public void GpuPreferenceIdentityGuardRequiresExactPidAndFileTime()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var snapshot = new ProcessInstanceRecoverySnapshot(42, startedAt);

        Assert.True(HostManagerSmartCoordinator.MatchesExactReceiptProcessIdentity(
            42,
            startKey,
            snapshot));
        Assert.False(HostManagerSmartCoordinator.MatchesExactReceiptProcessIdentity(
            43,
            startKey,
            snapshot));
        Assert.False(HostManagerSmartCoordinator.MatchesExactReceiptProcessIdentity(
            42,
            checked(startKey + 1),
            snapshot));
    }

    [Fact]
    public void CanonicalCpuScoreOrdersProcessesAcrossCompiledCcdCapacity()
    {
        var topology = CreateTwoCcdTopology();
        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            topology,
            CreateHardware(),
            CreateHardwareScores(),
            [
                CreateProcess("low", 20, 10, null, AutoPolicy()),
                CreateProcess("high", 10, 100, null, AutoPolicy())
            ],
            CreateCapacity());

        Assert.Collection(
            plan.Cpu,
            placement =>
            {
                Assert.Equal("high", placement.Process.TargetId);
                Assert.Equal(100, placement.CanonicalProcessScore);
                Assert.Equal(["core:0", "core:1"], placement.PhysicalCoreIds);
                Assert.Equal([1U, 2U], placement.Selector.CpuSetIds);
                Assert.Equal("automatic-single-ccd", placement.Source);
            },
            placement =>
            {
                Assert.Equal("low", placement.Process.TargetId);
                Assert.Equal(10, placement.CanonicalProcessScore);
                Assert.Equal(["core:2", "core:3"], placement.PhysicalCoreIds);
                Assert.Equal([3U, 4U], placement.Selector.CpuSetIds);
            });
    }

    [Fact]
    public void ManualPhysicalCoreLockIsNotReplacedByAutomaticPlacement()
    {
        var policy = AutoPolicy() with
        {
            EnabledMode = GpuPlacementPolicyModes.Manual,
            CpuManualLockedPositionIds = ["core:3"]
        };

        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(),
            CreateHardware(),
            CreateHardwareScores(),
            [CreateProcess("manual", 30, 999, null, policy)],
            CreateCapacity());

        var placement = Assert.Single(plan.Cpu);
        Assert.Equal(999, placement.CanonicalProcessScore);
        Assert.Equal(["core:3"], placement.PhysicalCoreIds);
        Assert.Equal([4U], placement.Selector.CpuSetIds);
        Assert.Equal("manual-physical-core-lock", placement.Source);
    }

    [Fact]
    public void CanonicalGpuScoreOrdersAutomaticPreferenceAcrossCompiledCapacity()
    {
        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(),
            CreateHardware(),
            CreateHardwareScores(),
            [
                CreateProcess("low", 20, null, 10, AutoPolicy()),
                CreateProcess("high", 10, null, 80, AutoPolicy())
            ],
            CreateCapacity());

        Assert.Collection(
            plan.GpuPreferences,
            placement =>
            {
                Assert.Equal("high", placement.Process.TargetId);
                Assert.Equal(80, placement.CanonicalProcessScore);
                Assert.False(placement.PreferIntegratedGpu);
                Assert.Equal(22UL, placement.TargetAdapterKey);
                Assert.Equal("automatic-gpu-class", placement.Source);
            },
            placement =>
            {
                Assert.Equal("low", placement.Process.TargetId);
                Assert.Equal(10, placement.CanonicalProcessScore);
                Assert.True(placement.PreferIntegratedGpu);
                Assert.Equal(11UL, placement.TargetAdapterKey);
            });
    }

    [Fact]
    public void TwoSameClassAdaptersKeepTheActualPlannerChoiceEvenWhenDisplayOrderChanges()
    {
        const ulong firstKey = 0x8123456789abcdef;
        const ulong secondKey = 0xfedcba9876543210;
        var hardware = CreateHardware();
        hardware = hardware with
        {
            Gpus = [hardware.Gpus[0] with { Name = "NVIDIA GeForce RTX 4090" }, hardware.Gpus[1]],
            GpuInventory = hardware.GpuInventory with
            {
                Adapters = [hardware.GpuInventory.Adapters[0] with { AdapterKey = firstKey },
                    hardware.GpuInventory.Adapters[1] with { AdapterKey = secondKey }]
            }
        };
        var policy = AutoPolicy() with { TargetGpu = GpuPlacementTargets.HighPerformanceGpu };
        var processes = new[] { CreateProcess("first", 10, null, 100, policy), CreateProcess("second", 20, null, 50, policy) };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, CreateHardwareScores(), processes, CreateCapacity());
        Assert.Equal(new[] { secondKey, firstKey }, plan.GpuPreferences.Select(p => p.TargetAdapterKey));
        Assert.All(plan.GpuPreferences, p => Assert.False(p.PreferIntegratedGpu));
        var reordered = hardware with { Gpus = hardware.Gpus.Reverse().ToArray(),
            GpuInventory = hardware.GpuInventory with { Adapters = hardware.GpuInventory.Adapters.Reverse().ToArray() } };
        var same = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), reordered, CreateHardwareScores(), processes, CreateCapacity());
        Assert.Equal(plan.GpuPreferences, same.GpuPreferences);
    }

    [Fact]
    public void CpuAndGpuPlacementConsumeTheirOwnCanonicalProcessScores()
    {
        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(),
            CreateHardware(),
            CreateHardwareScores(),
            [
                CreateProcess("cpu-first", 10, 100, 10, AutoPolicy()),
                CreateProcess("gpu-first", 20, 10, 80, AutoPolicy())
            ],
            CreateCapacity());

        Assert.Equal("cpu-first", plan.Cpu[0].Process.TargetId);
        Assert.Equal("gpu-first", plan.GpuPreferences[0].Process.TargetId);
        Assert.Equal(100, plan.Cpu[0].CanonicalProcessScore);
        Assert.Equal(80, plan.GpuPreferences[0].CanonicalProcessScore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShimOnlyPermissionPlansEveryInstanceWithoutRequiringRegistryPreference(bool enabled)
    {
        var policy = AutoPolicy() with
        {
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim],
            SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise,
            RuntimeHotSwitchEnabled = true
        };
        var first = CreateProcess("first", 10, null, 40, policy);
        var second = CreateProcess("second", 20, null, 20, policy) with { ExecutablePath = first.ExecutablePath };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), CreateHardware(), CreateHardwareScores(),
            [first, second], CreateCapacity(), runtimeGpuShimEnabled: enabled);
        Assert.Equal(enabled ? new[] { "first", "second" } : [], plan.GpuPreferences.Select(p => p.Process.TargetId));
        Assert.All(plan.GpuPreferences, p => Assert.NotEqual(0UL, p.TargetAdapterKey));
    }

    [Fact]
    public void MissingCanonicalScoresProduceNoNewPlacement()
    {
        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(),
            CreateHardware(),
            CreateHardwareScores(),
            [CreateProcess("no-data", 10, null, null, AutoPolicy())],
            CreateCapacity());

        Assert.Empty(plan.Cpu);
        Assert.Empty(plan.GpuPreferences);
    }

    [Theory]
    [InlineData(GpuPlacementTargets.SystemDefaultGpu)]
    [InlineData("GPU1")]
    public void UnsupportedExactRuntimeGpuTargetIsNotInventedAsWindowsPreference(string targetGpu)
    {
        var policy = AutoPolicy() with { TargetGpu = targetGpu };

        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(),
            CreateHardware(),
            CreateHardwareScores(),
            [CreateProcess("game", 10, null, 80, policy)],
            CreateCapacity());

        Assert.Empty(plan.GpuPreferences);
    }

    private static HostManagerAutomaticPlacementProcess CreateProcess(
        string targetId,
        int processId,
        double? cpuScore,
        double? gpuScore,
        ResolvedGpuPlacementPolicy policy)
        => new(
            targetId,
            $"software:{targetId}",
            targetId,
            processId,
            checked((ulong)DateTimeOffset.UtcNow.AddSeconds(-processId).ToFileTime()),
            targetId,
            $@"C:\Games\{targetId}.exe",
            true,
            policy,
            cpuScore,
            gpuScore.HasValue
                ? new Dictionary<ulong, double> { [22] = gpuScore.Value }
                : new Dictionary<ulong, double>(),
            new Dictionary<ulong, double>());

    private static ResolvedGpuPlacementPolicy AutoPolicy()
        => new(
            GpuPlacementPolicyModes.Auto,
            GpuPlacementRiskLevels.Low,
            [GpuPlacementProviderIds.WindowsGraphicsPreference],
            GpuPlacementSchedulingModes.Ordinary,
            GpuPlacementTargets.SystemDefaultGpu,
            GpuPlacementTargets.AutoIdleGpu,
            GpuPlacementRuntimeSchedulingModes.Ordinary,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            false,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            true,
            false,
            false,
            CpuMaximumOccupancyModes.SingleCcd,
            false,
            [],
            []);

    private static CompiledHostManagerPlacementCoordinatorRecreatePlan CreateCapacity()
        => new(16, 16, 16, 16, 4, 2, 16, 16);

    private static CompiledHardwareScorePlan CreateHardwareScores()
        => new(
            [],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [GpuPerformanceScoreIds.FromIndex(0)] = 50,
                [GpuPerformanceScoreIds.FromIndex(1)] = 100
            },
            "Test CPU",
            new Dictionary<int, double>
            {
                [0] = 100,
                [1] = 100,
                [2] = 50,
                [3] = 50
            });

    private static CpuTopologySnapshot CreateTwoCcdTopology()
    {
        var physical = Enumerable.Range(0, 4)
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
        var logical = Enumerable.Range(0, 4)
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
            "Test CPU",
            new CpuSpecificationModel(
                "Test CPU",
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
            physical,
            logical,
            []);
    }

    private static HardwareMetricSnapshot CreateHardware()
    {
        var observedAt = DateTimeOffset.UtcNow.UtcTicks;
        var adapters = new[]
        {
            CreateAdapter(0, 11, observedAt),
            CreateAdapter(1, 22, observedAt)
        };
        var sensors = new GpuSensorMetrics(
            new HardwareSensorProviderState("fixture", "ready", null),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        return new HardwareMetricSnapshot(
            DateTimeOffset.UtcNow,
            null!,
            null!,
            null!,
            [
                new GpuMetrics(0, "AMD Radeon(TM) Graphics", 0, 0, 0, 0, 0, 0, 0, 0, sensors),
                new GpuMetrics(1, "NVIDIA GeForce RTX 4090", 0, 0, 0, 0, 0, 0, 0, 0, sensors)
            ],
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.Current,
                7,
                observedAt,
                2,
                0,
                0,
                99,
                adapters),
            new Dictionary<string, MetricValue>());
    }

    private static SchedulingGpuAdapterObservation CreateAdapter(
        int index,
        ulong adapterKey,
        long observedAt)
        => new(
            index,
            adapterKey,
            SchedulingGpuCapabilityMask.Usage,
            SchedulingGpuMetricMask.Usage,
            SamplingObservationStatus.Current,
            SamplingObservationStatus.Unsupported,
            0,
            0,
            0,
            7,
            observedAt);
}
