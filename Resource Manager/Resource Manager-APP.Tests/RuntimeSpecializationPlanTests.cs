using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class RuntimeSpecializationPlanTests
{
    [Fact]
    public void CompiledGpuPolicyPreservesKindDefaultForPersistedNullWithoutOverridingExplicitValues()
    {
        var game = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("game", "Game", SoftwareKinds.Game) with
        { RuntimeHotSwitchEnabled = null };
        var highPerformance = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("work", "Work", SoftwareKinds.HighPerformance) with
        { RuntimeHotSwitchEnabled = null };
        var ordinary = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("ordinary", "Ordinary", SoftwareKinds.Other) with
        { RuntimeHotSwitchEnabled = null };
        var explicitOn = game with { SoftwareId = "explicit-on", RuntimeHotSwitchEnabled = true };
        var explicitOff = ordinary with { SoftwareId = "explicit-off", RuntimeHotSwitchEnabled = false };
        var process = new GpuPlacementProcessPolicy("game", "renderer", "renderer.exe", @"C:\Game\renderer.exe",
            false, GpuPlacementPolicyModes.Inherit, GpuPlacementRiskLevels.Low,
            GpuPlacementProviderIds.Defaults, GpuPlacementTargets.AutoIdleGpu,
            GpuPlacementExplicitSelectionModes.DefaultSkip, null, DateTimeOffset.UnixEpoch);
        var document = new GpuPlacementPolicyDocument(GpuPlacementPolicyDocumentVersions.Current,
            [game, highPerformance, ordinary, explicitOn, explicitOff], [process], DateTimeOffset.UnixEpoch);
        var topology = new CpuTopologySnapshot(DateTimeOffset.UnixEpoch, "fixture",
            new CpuSpecificationModel("fixture", "fixture", "fixture", 0, 0, null, null, null, null, "fixture"),
            "fixture", "fixture", "fixture", "fixture", "fixture", 0, 0, 0, false, [], [], [], []);

        var plan = RuntimePlanCompiler.CompileGpuPlacementPlan(true, document, topology);

        Assert.False(plan.Resolve("game", "Game", SoftwareKinds.Game, null).RuntimeHotSwitchEnabled);
        Assert.False(plan.Resolve("game", "Game", SoftwareKinds.Game, "renderer").RuntimeHotSwitchEnabled);
        Assert.Equal(GpuPlacementPolicyModes.Auto,
            plan.Resolve("game", "Game", SoftwareKinds.Game, "renderer").EnabledMode);
        Assert.False(plan.Resolve("work", "Work", SoftwareKinds.HighPerformance, null).RuntimeHotSwitchEnabled);
        Assert.True(plan.Resolve("ordinary", "Ordinary", SoftwareKinds.Other, null).RuntimeHotSwitchEnabled);
        Assert.True(plan.Resolve("explicit-on", "Game", SoftwareKinds.Game, null).RuntimeHotSwitchEnabled);
        Assert.False(plan.Resolve("explicit-off", "Ordinary", SoftwareKinds.Other, null).RuntimeHotSwitchEnabled);
    }

    [Fact]
    public void OptimizationModePlan_CompilesDistinctExecutionCapabilities()
    {
        var normal = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal);
        var memoryOnly = CompiledOptimizationModePlan.Compile(AppOptimizationModes.MemoryOnly);
        var smart = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Smart);

        Assert.False(normal.SchedulingEnabled);
        Assert.False(normal.NonAdaptedMemoryPriorityEnabled);
        Assert.False(normal.HardwarePlacementEnabled);

        Assert.Equal("limited", memoryOnly.Mode);
        Assert.True(memoryOnly.SchedulingEnabled);
        Assert.False(memoryOnly.ExternalAdaptedMemoryActionsEnabled);
        Assert.True(memoryOnly.ResourceManagerSelfMemoryActionsEnabled);
        Assert.True(memoryOnly.AutomaticMemoryCleanupEnabled);
        Assert.False(memoryOnly.VramResourceActionsEnabled);
        Assert.False(memoryOnly.SoftwareSchedulingEnabled);
        Assert.False(memoryOnly.AutomaticProcessPoliciesEnabled);
        Assert.False(memoryOnly.HardwarePlacementEnabled);
        Assert.True(memoryOnly.NonAdaptedMemoryPriorityEnabled);

        Assert.False(smart.ExternalAdaptedMemoryActionsEnabled);
        Assert.True(smart.ResourceManagerSelfMemoryActionsEnabled);
        Assert.True(smart.AutomaticMemoryCleanupEnabled);
        Assert.True(smart.VramResourceActionsEnabled);
        Assert.True(smart.SoftwareSchedulingEnabled);
        Assert.True(smart.AutomaticProcessPoliciesEnabled);
        Assert.True(smart.HardwarePlacementEnabled);
        Assert.True(smart.NonAdaptedMemoryPriorityEnabled);
    }

    [Theory]
    [InlineData(AppOptimizationModes.Normal, false, false, false)]
    [InlineData(AppOptimizationModes.MemoryOnly, true, false, false)]
    [InlineData(AppOptimizationModes.CpuOnly, false, true, false)]
    [InlineData(AppOptimizationModes.GpuOnly, false, false, true)]
    [InlineData(AppOptimizationModes.MemoryCpu, true, true, false)]
    [InlineData(AppOptimizationModes.MemoryGpu, true, false, true)]
    [InlineData(AppOptimizationModes.CpuGpu, false, true, true)]
    [InlineData(AppOptimizationModes.Smart, true, true, true)]
    public void OptimizationModePlan_OnlyEnablesSelectedSchedulingDomains(
        string mode,
        bool memory,
        bool cpu,
        bool gpu)
    {
        var plan = CompiledOptimizationModePlan.Compile(mode);

        Assert.Equal(mode, plan.Mode);
        Assert.Equal(memory || cpu || gpu, plan.SchedulingEnabled);
        Assert.Equal(memory, plan.MemorySchedulingEnabled);
        Assert.Equal(memory, plan.AutomaticMemoryCleanupEnabled);
        Assert.Equal(memory, plan.NonAdaptedMemoryPriorityEnabled);
        Assert.Equal(cpu, plan.CpuSchedulingEnabled);
        Assert.Equal(cpu, plan.AutomaticProcessPoliciesEnabled);
        Assert.Equal(cpu, plan.CpuPlacementEnabled);
        Assert.Equal(gpu, plan.GpuSchedulingEnabled);
        Assert.Equal(gpu, plan.VramResourceActionsEnabled);
        Assert.Equal(gpu, plan.GpuPlacementEnabled);
        Assert.Equal(cpu || gpu, plan.SoftwareSchedulingEnabled);
    }

    [Fact]
    public void AdapterDispatchPlan_ResolvesPrecompiledRoutes()
    {
        var plan = new CompiledAdapterDispatchPlan(
            new Dictionary<string, CompiledAdapterDispatchRoute>(StringComparer.OrdinalIgnoreCase)
            {
                ["software-a"] = CompiledAdapterDispatchRoute.SoftwareLevelScheduler
            },
            new Dictionary<string, IReadOnlyList<AdapterCpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase)
            {
                ["software-a"] = [AdapterCpuSchedulingGrade.Normal, AdapterCpuSchedulingGrade.Optimize]
            },
            new Dictionary<string, IReadOnlyList<AdapterGpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase)
            {
                ["software-a"] = [AdapterGpuSchedulingGrade.Normal]
            });

        Assert.Equal(CompiledAdapterDispatchRoute.SoftwareLevelScheduler, plan.ResolveRoute("SOFTWARE-A"));
        Assert.Equal(CompiledAdapterDispatchRoute.ConstraintActions, plan.ResolveRoute("missing"));
        Assert.Equal(
            [AdapterCpuSchedulingGrade.Normal, AdapterCpuSchedulingGrade.Optimize],
            plan.ResolveSupportedCpuGrades("software-a"));
        Assert.Equal(
            [AdapterGpuSchedulingGrade.Normal],
            plan.ResolveSupportedGpuGrades("software-a"));
        Assert.Empty(plan.ResolveSupportedCpuGrades("missing"));
        Assert.Empty(plan.ResolveSupportedGpuGrades("missing"));
    }

    [Fact]
    public void ResourceManagerSelfDescriptor_OnlySupportsNormalAndOptimizePerDimension()
    {
        Assert.Equal(
            [AdapterCpuSchedulingGrade.Normal, AdapterCpuSchedulingGrade.Optimize],
            ResourceManagerSelfDescriptor.SupportedCpuSchedulingGrades);
        Assert.Equal(
            [AdapterGpuSchedulingGrade.Normal, AdapterGpuSchedulingGrade.Optimize],
            ResourceManagerSelfDescriptor.SupportedGpuSchedulingGrades);
    }

    [Fact]
    public void GpuPlacementPlan_ProcessPolicyOverridesSoftwarePolicy()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy(
            "software-a",
            "Software A",
            SoftwareKinds.Other) with
        {
            TargetGpu = GpuPlacementTargets.HighPerformanceGpu,
            ProcessOverrideAllowed = true
        };
        var processPolicy = new GpuPlacementProcessPolicy(
            "software-a",
            "proc-key",
            "worker.exe",
            @"C:\Apps\worker.exe",
            Inherit: false,
            GpuPlacementPolicyModes.Manual,
            GpuPlacementRiskLevels.Low,
            [GpuPlacementProviderIds.D3dDeviceCreateShim],
            "GPU1",
            GpuPlacementExplicitSelectionModes.AllowLow,
            BaseScoreOverride: null,
            DateTimeOffset.UnixEpoch);
        var plan = new CompiledGpuPlacementPlan(
            true,
            new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["software-a"] = ResolvedGpuPlacementPolicy.FromSoftware(softwarePolicy)
            },
            new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                [CompiledBaseScorePlan.CreateProcessPolicyKey("software-a", "proc-key")] =
                    ResolvedGpuPlacementPolicy.FromProcess(processPolicy, softwarePolicy)
            });

        var resolved = plan.Resolve("software-a", "Software A", SoftwareKinds.Other, "proc-key");

        Assert.Equal("GPU1", resolved.TargetGpu);
        Assert.True(resolved.AllowsRuntimeShimExecution());
    }

    [Fact]
    public void BaseScorePlan_ProcessScoreWinsOverSoftwareScore()
    {
        var plan = new CompiledBaseScorePlan(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["software-a"] = 40
            },
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [CompiledBaseScorePlan.CreateProcessPolicyKey("software-a", "proc-key")] = 85
            });

        Assert.Equal(85, plan.ResolveBaseScore("software-a", SoftwareKinds.Other, "proc-key"));
        Assert.Equal(40, plan.ResolveBaseScore("software-a", SoftwareKinds.Other, "missing"));
    }

    [Fact]
    public void HardwareScorePlan_UsesCompiledCpuCoreOverrides()
    {
        var plan = new CompiledHardwareScorePlan(
            [AppGpuPerformanceUseCases.General],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            "Test CPU",
            new Dictionary<int, double>
            {
                [2] = 130,
                [3] = double.NaN
            });

        Assert.Equal(130, plan.ResolveCpuPerformanceScore(2, 100));
        Assert.Equal(0, plan.ResolveCpuPerformanceScore(3, 100));
        Assert.Equal(80, plan.ResolveCpuPerformanceScore(4, 80));
    }

    [Fact]
    public void RuntimePlanProvider_PublishesWholePlan()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var hostManager = CompileHostManagerPlan(7);
        var next = CompiledRuntimePlan.Default with
        {
            Version = 7,
            Reason = "settings-saved",
            HostManager = hostManager
        };

        provider.Publish(next);

        Assert.Same(next, provider.Current);
        Assert.Equal(
            HostManagerDeploymentStatus.Initializing,
            deployment.Snapshot.ResourceScheduler.Status);
    }

    [Fact]
    public void DiagnosticsPlan_CompilesDebugHotPathFlags()
    {
        var disabled = CompiledDiagnosticsPlan.Default;
        var enabled = disabled with
        {
            DebugModeEnabled = true,
            DebugLogEnabled = true,
            HostManagerSmartCoordinatorScoreOnlyEnabled = true,
            HostManagerSmartCoordinatorPerformanceLogEnabled = true
        };

        Assert.False(disabled.HostManagerSmartCoordinatorPerformanceLogEnabled);
        Assert.True(enabled.DebugLogEnabled);
        Assert.True(enabled.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.True(enabled.HostManagerSmartCoordinatorPerformanceLogEnabled);
    }

    [Fact]
    public void MonitoringPlan_ResolvesColumnsByViewMode()
    {
        var plan = CompiledMonitoringPlan.Default with
        {
            SoftwareTableColumnIds = ["name", "cpu"],
            ProcessTableColumnIds = ["name", "pid", "cpu"]
        };

        Assert.Equal(["name", "cpu"], plan.ResolveTableColumnIds("software"));
        Assert.Equal(["name", "pid", "cpu"], plan.ResolveTableColumnIds("process"));
    }

    [Fact]
    public void MonitoringSourceZoneRegistry_CanRegisterAndUnregisterRuntimeZones()
    {
        var registry = new MonitoringSourceZoneRegistry([]);
        var zone = new MonitoringSourceZone("collector.test.dynamic");

        Assert.True(registry.Register(zone));
        Assert.Contains("collector.test.dynamic", registry.GetSourceIds());
        Assert.True(registry.CanRead("collector.test.dynamic"));

        Assert.True(registry.Unregister("collector.test.dynamic"));
        Assert.DoesNotContain("collector.test.dynamic", registry.GetSourceIds());
    }

    private static CompiledHostManagerPlan CompileHostManagerPlan(ulong planEpoch)
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            buffer.ToArray(),
            "test/default.json");
        return new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(),
            planEpoch,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
    }
}
