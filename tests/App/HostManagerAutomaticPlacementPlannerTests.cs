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
    public void PlacementPlanningOnlyProducesSelectedHardwareDomain()
    {
        var process = CreateProcess("renderer", 10, 40, 40, AutoPolicy());
        var cpuOnly = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(), CreateHardware(), CreateHardwareScores(),
            [process], CreateCapacity(), runtimeGpuShimEnabled: true,
            gpuOverflow: new(95, 80), cpuPlacementEnabled: true, gpuPlacementEnabled: false);
        var gpuOnly = HostManagerAutomaticPlacementPlanner.Plan(
            null, CreateHardware(), CreateHardwareScores(),
            [process], CreateCapacity(), runtimeGpuShimEnabled: true,
            gpuOverflow: new(95, 80), cpuPlacementEnabled: false, gpuPlacementEnabled: true);

        Assert.Single(cpuOnly.Cpu);
        Assert.Empty(cpuOnly.Gpu);
        Assert.Empty(gpuOnly.Cpu);
        Assert.Single(gpuOnly.Gpu);
    }

    [Fact]
    public void SingleGpuHasNoRuntimeMigrationTarget()
    {
        var hardware = CreateHardware();
        hardware = hardware with
        {
            Gpus = [hardware.Gpus[1]],
            GpuInventory = hardware.GpuInventory with
            {
                ObservedCount = 1,
                Adapters = [hardware.GpuInventory.Adapters[1]]
            }
        };
        var process = CreateProcess("renderer", 10, null, 80, AutoPolicy());

        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            null, hardware, CreateHardwareScores(), [process], CreateCapacity(),
            runtimeGpuShimEnabled: true, gpuOverflow: new(95, 80),
            cpuPlacementEnabled: false, gpuPlacementEnabled: true);

        Assert.True(hardware.GpuInventory.IsCurrentComplete());
        Assert.Empty(plan.Gpu);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnknownCapacityIsNotInventedAsOneButExplicitTargetRemainsAvailable(double score)
    {
        var hardware = CreateHardware();
        hardware = hardware with { GpuInventory = hardware.GpuInventory with
        { Adapters = [hardware.GpuInventory.Adapters[0], hardware.GpuInventory.Adapters[1] with { UsagePercent = 99 }] } };
        var scores = CreateHardwareScores() with { GpuPerformanceScoresByGpuId = new Dictionary<string, double>
        { ["gpu:0"] = score, ["gpu:1"] = 100 } };
        var process = CreateProcess("tiny", 20, null, 1, AutoPolicy()) with
        { ObservedGpuUsagePercent = new Dictionary<ulong, double> { [22] = 0.001 } };
        var automatic = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, scores,
            [process], CreateCapacity(), true, new(95, 80));
        Assert.Equal(22UL, Assert.Single(automatic.Gpu).TargetAdapterKey);
        var explicitPlan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, scores,
            [process with { Policy = process.Policy with { TargetGpu = GpuPlacementTargets.IntegratedGpu } }],
            CreateCapacity(), true, new(95, 80));
        Assert.Equal(11UL, Assert.Single(explicitPlan.Gpu).TargetAdapterKey);
    }

    [Fact]
    public void IdleUmaRendererWithDiscreteResidueIsNotMovedToUmaAgain()
    {
        var hardware = CreateHardware();
        hardware = hardware with { GpuInventory = hardware.GpuInventory with
        { Adapters = [hardware.GpuInventory.Adapters[0], hardware.GpuInventory.Adapters[1] with
        {
            CapacityStatus = SamplingObservationStatus.Current,
            CapabilityMask = SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
            ValidMetricMask = SchedulingGpuMetricMask.Usage | SchedulingGpuMetricMask.UsedDedicatedMemory | SchedulingGpuMetricMask.TotalDedicatedMemory,
            UsedDedicatedMemoryBytes = 6_900_162_560, TotalDedicatedMemoryBytes = 8_546_942_976
        }] }};
        var process = CreateProcess("resident-renderer", 20, null, 20, AutoPolicy()) with
        {
            ObservedGpuUsagePercent = new Dictionary<ulong, double> { [11] = 0, [22] = 0 },
            ObservedDedicatedMemoryBytes = new Dictionary<ulong, double> { [11] = 78_598_144, [22] = 31_088_640 }
        };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, CreateHardwareScores(),
            [process], CreateCapacity(), true, new(95, 80));
        var placement = Assert.Single(plan.Gpu);
        Assert.Equal(11UL, placement.ObservedAdapterKey);
        Assert.Equal(placement.ObservedAdapterKey, placement.TargetAdapterKey);
    }

    [Fact]
    public void ExplicitGpuClassRetainsActualAdapterForAlreadyPlacedSuppression()
    {
        var process = CreateProcess("integrated", 20, null, 20, AutoPolicy() with { TargetGpu = GpuPlacementTargets.IntegratedGpu }) with
        { ObservedGpuUsagePercent = new Dictionary<ulong, double> { [11] = 5 } };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), CreateHardware(), CreateHardwareScores(),
            [process], CreateCapacity(), true, new(95, 80));
        var placement = Assert.Single(plan.Gpu);
        Assert.Equal(11UL, placement.TargetAdapterKey);
        Assert.Equal(placement.TargetAdapterKey, placement.ObservedAdapterKey);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    public void IntegratedDedicatedBytesDoNotProveTheWholeMigrationFitsDedicatedVram(ulong dedicatedBytes)
    {
        var hardware = CreateHardware();
        hardware = hardware with { GpuInventory = hardware.GpuInventory with
        { Adapters = [hardware.GpuInventory.Adapters[0], hardware.GpuInventory.Adapters[1] with
        {
            CapacityStatus = SamplingObservationStatus.Current,
            CapabilityMask = SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
            ValidMetricMask = SchedulingGpuMetricMask.Usage | SchedulingGpuMetricMask.UsedDedicatedMemory | SchedulingGpuMetricMask.TotalDedicatedMemory,
            UsedDedicatedMemoryBytes = 84, TotalDedicatedMemoryBytes = 100
        }] }};
        var process = CreateProcess("integrated", 20, null, 20, AutoPolicy()) with
        {
            ObservedGpuUsagePercent = new Dictionary<ulong, double> { [11] = 5 },
            ObservedDedicatedMemoryBytes = new Dictionary<ulong, double> { [11] = dedicatedBytes }
        };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, CreateHardwareScores(),
            [process], CreateCapacity(), true, new(95, 85));
        Assert.Equal(11UL, Assert.Single(plan.Gpu).TargetAdapterKey);
    }

    [Theory]
    [InlineData(94.9, 79, false)]
    [InlineData(95, 0, true)]
    [InlineData(0, 80, true)]
    [InlineData(95, 80, true)]
    public void EitherActualPressureTriggersOverflowOfLowestPriority(double usage, ulong memory, bool overflow)
    {
        var hardware = CreateHardware();
        hardware = hardware with { GpuInventory = hardware.GpuInventory with
        {
            Adapters = [hardware.GpuInventory.Adapters[0], hardware.GpuInventory.Adapters[1] with
            {
                UsagePercent = usage, CapacityStatus = SamplingObservationStatus.Current,
                CapabilityMask = SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
                ValidMetricMask = SchedulingGpuMetricMask.Usage | SchedulingGpuMetricMask.UsedDedicatedMemory | SchedulingGpuMetricMask.TotalDedicatedMemory,
                UsedDedicatedMemoryBytes = memory, TotalDedicatedMemoryBytes = 100
            }]
        }};
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, CreateHardwareScores(),
            [CreateProcess("high", 10, null, 80, AutoPolicy()), CreateProcess("low", 20, null, 10, AutoPolicy())],
            CreateCapacity(), true, new(95, 80));
        Assert.Equal(22UL, plan.Gpu.Single(p => p.Process.ProcessId == 10).TargetAdapterKey);
        Assert.All(plan.Gpu, p => Assert.Equal(22UL, p.ObservedAdapterKey));
        Assert.Equal(overflow ? 11UL : 22UL, plan.Gpu.Single(p => p.Process.ProcessId == 20).TargetAdapterKey);
    }

    [Fact]
    public void OverflowNeverInventsReleasedSharedMemoryOrMovesEveryProcessAtOnce()
    {
        var hardware = CreateHardware();
        hardware = hardware with { GpuInventory = hardware.GpuInventory with
        { Adapters = [hardware.GpuInventory.Adapters[0], hardware.GpuInventory.Adapters[1] with { UsagePercent = 99 }] }};
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, CreateHardwareScores(),
            [CreateProcess("one", 10, null, 10, AutoPolicy()), CreateProcess("two", 20, null, 20, AutoPolicy()),
             CreateProcess("three", 30, null, 30, AutoPolicy())], CreateCapacity(), true, new(95, 85));
        Assert.Single(plan.Gpu, p => p.TargetAdapterKey == 11);
        Assert.Equal("one", plan.Gpu.Single(p => p.TargetAdapterKey == 11).Process.TargetId);
    }

    [Fact]
    public void UnavailableRouteCannotEnterMovementCandidatesOrConsumeOverflowSlot()
    {
        var hardware = CreateHardware();
        hardware = hardware with { GpuInventory = hardware.GpuInventory with
        { Adapters = [hardware.GpuInventory.Adapters[0], hardware.GpuInventory.Adapters[1] with { UsagePercent = 99 }] }};
        var first = CreateProcess("first", 10, null, 10, AutoPolicy()) with
        {
            CanApplyPhysicalPlacement = false
        };
        var second = CreateProcess("second", 20, null, 20, AutoPolicy());
        var third = CreateProcess("third", 30, null, 30, AutoPolicy());
        string Selected(params HostManagerAutomaticPlacementProcess[] processes)
        {
            var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware,
                CreateHardwareScores(), processes, CreateCapacity(), true, new(95, 80));
            return Assert.Single(plan.Gpu, item => item.TargetAdapterKey == 11).Process.TargetId;
        }
        Assert.Equal("first", Selected(first, second, third));
        first = first with { CanMigrateGpu = false };
        Assert.Equal("second", Selected(first, second, third));
        second = second with { CanMigrateGpu = false };
        Assert.Equal("third", Selected(first, second, third));
        var unavailable = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware,
            CreateHardwareScores(), [first, second, third with { CanMigrateGpu = false }], CreateCapacity(), true, new(95, 80));
        Assert.Empty(unavailable.Gpu);
    }

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

    [Theory]
    [InlineData(0, 11UL)]
    [InlineData(95, 33UL)]
    public void OverflowTraversesPerformanceTiersRatherThanGpuClasses(double middleUsage, ulong destination)
    {
        var hardware = CreateHardware();
        hardware = hardware with
        {
            Gpus = [.. hardware.Gpus, hardware.Gpus[0] with { Index = 2 }],
            GpuInventory = hardware.GpuInventory with
            {
                ObservedCount = 3,
                Adapters = [hardware.GpuInventory.Adapters[0] with { UsagePercent = middleUsage },
                    hardware.GpuInventory.Adapters[1] with { UsagePercent = 95 },
                    CreateAdapter(2, 33, hardware.GpuInventory.ObservedAtUtcTicks)]
            }
        };
        var scores = new CompiledHardwareScorePlan([], new Dictionary<string, double>
        {
            [GpuPerformanceScoreIds.FromIndex(0)] = 50,
            [GpuPerformanceScoreIds.FromIndex(1)] = 100,
            [GpuPerformanceScoreIds.FromIndex(2)] = 25
        }, "Test CPU", new Dictionary<int, double>());
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, scores,
            [CreateProcess("low", 20, null, 10, AutoPolicy())], CreateCapacity(), true, new(95, 85));
        Assert.Equal(destination, Assert.Single(plan.Gpu).TargetAdapterKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedOrNonContributingLowestPriorityDoesNotStarveAnEligibleVictim(bool blocked)
    {
        var hardware = CreateHardware();
        hardware = hardware with { GpuInventory = hardware.GpuInventory with
        { Adapters = [hardware.GpuInventory.Adapters[0], hardware.GpuInventory.Adapters[1] with { UsagePercent = 99 }] }};
        var low = CreateProcess("low", 10, null, 1, AutoPolicy()) with
        {
            CanMigrateGpu = !blocked,
            ObservedGpuUsagePercent = new Dictionary<ulong, double> { [22] = blocked ? 1 : 0 }
        };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, CreateHardwareScores(),
            [low, CreateProcess("contributes", 20, null, 20, AutoPolicy())], CreateCapacity(), true, new(95, 85));
        if (blocked) Assert.DoesNotContain(plan.Gpu, p => p.Process.ProcessId == 10);
        else Assert.Equal(22UL, plan.Gpu.Single(p => p.Process.ProcessId == 10).TargetAdapterKey);
        Assert.Equal(11UL, plan.Gpu.Single(p => p.Process.ProcessId == 20).TargetAdapterKey);
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
            CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));

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
            CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));

        var placement = Assert.Single(plan.Cpu);
        Assert.Equal(999, placement.CanonicalProcessScore);
        Assert.Equal(["core:3"], placement.PhysicalCoreIds);
        Assert.Equal([4U], placement.Selector.CpuSetIds);
        Assert.Equal("manual-physical-core-lock", placement.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisabledGpuSelectionDoesNotDisableCpuPlacement(bool manualLock)
    {
        var policy = AutoPolicy() with
        {
            EnabledMode = GpuPlacementPolicyModes.Disabled,
            CpuManualLockedPositionIds = manualLock ? ["core:3"] : []
        };
        var process = CreateProcess("cpu-only", 31, 40, 20, policy);
        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(), CreateHardware(), CreateHardwareScores(),
            [process], CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 80));

        var cpu = Assert.Single(plan.Cpu);
        Assert.Equal(manualLock ? "manual-physical-core-lock" : "automatic-single-ccd", cpu.Source);
        if (manualLock) Assert.Equal(["core:3"], cpu.PhysicalCoreIds);
        Assert.Empty(plan.Gpu);

        var unavailable = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(), CreateHardware(), CreateHardwareScores(),
            [process with { CanApplyPhysicalPlacement = false }], CreateCapacity(), true, new(95, 80));
        Assert.Empty(unavailable.Cpu);
    }

    [Fact]
    public void CanonicalGpuScoreOrdersRuntimePlacementAcrossCompiledCapacity()
    {
        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(),
            CreateHardware(),
            CreateHardwareScores(),
            [
                CreateProcess("low", 20, null, 10, AutoPolicy()),
                CreateProcess("high", 10, null, 80, AutoPolicy())
            ],
            CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));

        Assert.Collection(
            plan.Gpu,
            placement =>
            {
                Assert.Equal("high", placement.Process.TargetId);
                Assert.Equal(80, placement.CanonicalProcessScore);
                Assert.False(placement.PreferIntegratedGpu);
                Assert.Equal(22UL, placement.TargetAdapterKey);
                Assert.Equal("automatic-gpu-performance-overflow", placement.Source);
            },
            placement =>
            {
                Assert.Equal("low", placement.Process.TargetId);
                Assert.Equal(10, placement.CanonicalProcessScore);
                Assert.False(placement.PreferIntegratedGpu);
                Assert.Equal(22UL, placement.TargetAdapterKey);
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
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), hardware, CreateHardwareScores(), processes, CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));
        Assert.Equal(new[] { secondKey, secondKey }, plan.Gpu.Select(p => p.TargetAdapterKey));
        Assert.All(plan.Gpu, p => Assert.False(p.PreferIntegratedGpu));
        var reordered = hardware with { Gpus = hardware.Gpus.Reverse().ToArray(),
            GpuInventory = hardware.GpuInventory with { Adapters = hardware.GpuInventory.Adapters.Reverse().ToArray() } };
        var same = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), reordered, CreateHardwareScores(), processes, CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));
        Assert.Equal(plan.Gpu, same.Gpu);
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
            CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));

        Assert.Equal("cpu-first", plan.Cpu[0].Process.TargetId);
        Assert.Equal("gpu-first", plan.Gpu[0].Process.TargetId);
        Assert.Equal(100, plan.Cpu[0].CanonicalProcessScore);
        Assert.Equal(80, plan.Gpu[0].CanonicalProcessScore);
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
            [first, second], CreateCapacity(), runtimeGpuShimEnabled: enabled, gpuOverflow: new(95, 85));
        Assert.Equal(enabled ? new[] { "first", "second" } : [], plan.Gpu.Select(p => p.Process.TargetId));
        Assert.All(plan.Gpu, p => Assert.NotEqual(0UL, p.TargetAdapterKey));
    }

    [Fact]
    public void MissingCanonicalScoresProduceNoNewPlacement()
    {
        var plan = HostManagerAutomaticPlacementPlanner.Plan(
            CreateTwoCcdTopology(),
            CreateHardware(),
            CreateHardwareScores(),
            [CreateProcess("no-data", 10, null, null, AutoPolicy())],
            CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));

        Assert.Empty(plan.Cpu);
        Assert.Empty(plan.Gpu);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalRuntimeActionDoesNotRequireLegacyShimProvider(bool runtimeEnabled)
    {
        var policy = AutoPolicy() with { AllowedProviders = [GpuPlacementProviderIds.WindowsGraphicsPreference] };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), CreateHardware(), CreateHardwareScores(),
            [CreateProcess("startup-only", 10, null, 80, policy)], CreateCapacity(),
            runtimeGpuShimEnabled: runtimeEnabled, gpuOverflow: new(95, 80));
        Assert.Equal(runtimeEnabled ? 1 : 0, plan.Gpu.Count);
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
            CreateCapacity(), runtimeGpuShimEnabled: true, gpuOverflow: new(95, 85));

        Assert.Empty(plan.Gpu);
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
            gpuScore.HasValue ? new Dictionary<ulong, double> { [22] = 1 } : new Dictionary<ulong, double>())
        { ObservedDedicatedMemoryBytes = gpuScore.HasValue ? new Dictionary<ulong, double> { [22] = 1 } : new Dictionary<ulong, double>() };

    private static ResolvedGpuPlacementPolicy AutoPolicy()
        => new(
            GpuPlacementPolicyModes.Auto,
            GpuPlacementRiskLevels.Low,
            [GpuPlacementProviderIds.D3dDeviceCreateShim],
            GpuPlacementSchedulingModes.Precise,
            GpuPlacementTargets.SystemDefaultGpu,
            GpuPlacementTargets.AutoIdleGpu,
            GpuPlacementRuntimeSchedulingModes.Precise,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            true,
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
