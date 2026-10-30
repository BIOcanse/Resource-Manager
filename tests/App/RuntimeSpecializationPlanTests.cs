using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.RuntimeSpecialization;
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
            false,
            GpuPlacementProviderIds.Defaults,
            null, DateTimeOffset.UnixEpoch);
        var document = new GpuPlacementPolicyDocument(GpuPlacementPolicyDocumentVersions.Current,
            [game, highPerformance, ordinary, explicitOn, explicitOff], [process], DateTimeOffset.UnixEpoch);
        var topology = new CpuTopologySnapshot(DateTimeOffset.UnixEpoch, "fixture",
            new CpuSpecificationModel("fixture", "fixture", "fixture", 0, 0, null, null, null, null, "fixture"),
            "fixture", "fixture", "fixture", "fixture", "fixture", 0, 0, 0, false, [], [], [], []);

        var plan = RuntimePlanCompiler.CompileGpuPlacementPlan(true, document, topology);

        Assert.False(plan.Resolve("game", "Game", SoftwareKinds.Game, null).RuntimeHotSwitchEnabled);
        Assert.False(plan.Resolve("game", "Game", SoftwareKinds.Game, "renderer").RuntimeHotSwitchEnabled);
        Assert.False(plan.Resolve("game", "Game", SoftwareKinds.Game, "renderer").GpuSchedulingExcluded);
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
        Assert.Equal(0, (byte)memoryOnly.Capabilities & (1 << 2));
        Assert.Equal(0, (byte)memoryOnly.Capabilities & (1 << 3));
        Assert.False(memoryOnly.AutomaticProcessPoliciesEnabled);
        Assert.False(memoryOnly.HardwarePlacementEnabled);
        Assert.True(memoryOnly.NonAdaptedMemoryPriorityEnabled);

        Assert.False(smart.ExternalAdaptedMemoryActionsEnabled);
        Assert.True(smart.ResourceManagerSelfMemoryActionsEnabled);
        Assert.True(smart.AutomaticMemoryCleanupEnabled);
        Assert.Equal(0, (byte)smart.Capabilities & (1 << 2));
        Assert.Equal(0, (byte)smart.Capabilities & (1 << 3));
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
        Assert.Equal(0, (byte)plan.Capabilities & (1 << 2));
        Assert.Equal(gpu, plan.GpuPlacementEnabled);
        Assert.Equal(0, (byte)plan.Capabilities & (1 << 3));
    }

    [Fact]
    public void GpuPlacementPlan_ProcessPolicyOverridesSoftwarePolicy()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy(
            "software-a",
            "Software A",
            SoftwareKinds.Other) with
        {
            StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu,
            ProcessOverrideAllowed = true
        };
        var processPolicy = new GpuPlacementProcessPolicy(
            "software-a",
            "proc-key",
            "worker.exe",
            @"C:\Apps\worker.exe",
            Inherit: false,
            [GpuPlacementProviderIds.D3dDeviceCreateShim],
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

        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, resolved.StartupTargetGpu);
        Assert.False(resolved.GpuSchedulingExcluded);
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
    public void ScoreOnlyDiagnosticIsIndependentOfDebugModeButRemainsRuntimeCapabilityConstrained()
    {
        var persisted = AppSettingsDefaults.Create() with
        {
            Debug = new AppDebugSettings(
                DebugModeEnabled: false,
                DebugLogEnabled: true,
                HostManagerSmartCoordinatorScoreOnlyEnabled: true,
                HostManagerSmartCoordinatorPerformanceLogEnabled: true)
        };
        var normalized = AppSettingsNormalizer.Normalize(persisted);
        var enabledProjection = AppSettingsRuntimeCapabilityProjector.Evaluate(
            normalized,
            new RuntimeExecutionCapabilityPolicy(true, true, true));

        var diagnostics = RuntimePlanCompiler.CompileDiagnosticsPlan(
            enabledProjection.Settings.Debug);

        Assert.False(diagnostics.DebugModeEnabled);
        Assert.False(diagnostics.DebugLogEnabled);
        Assert.True(diagnostics.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.False(diagnostics.HostManagerSmartCoordinatorPerformanceLogEnabled);

        var constrainedProjection = AppSettingsRuntimeCapabilityProjector.Evaluate(
            normalized,
            new RuntimeExecutionCapabilityPolicy(false, false, false));
        Assert.Contains(
            "debug.hostManagerSmartCoordinatorScoreOnlyEnabled",
            constrainedProjection.ConstrainedPaths);
        Assert.False(
            RuntimePlanCompiler.CompileDiagnosticsPlan(
                constrainedProjection.Settings.Debug)
                .HostManagerSmartCoordinatorScoreOnlyEnabled);
    }

    [Fact]
    public void MonitoringPlanContainsOnlyBackendProviderConfiguration()
    {
        Assert.Equal(new[] { "FirmwareProviders", "RegisteredSourceZoneIds" },
            typeof(CompiledMonitoringPlan).GetProperties().Where(property => property.GetMethod?.IsStatic == false)
                .Select(property => property.Name).Order());
        Assert.NotNull(CompiledMonitoringPlan.Default.FirmwareProviders);
    }
    [Fact]
    public void MonitoringSourceZoneRegistry_CanRegisterAndUnregisterRuntimeZones()
    {
        var registry = new MonitoringSourceZoneRegistry([]);
        var zone = new MonitoringSourceZone("collector.test.dynamic");

        Assert.True(registry.Register(zone));
        Assert.Contains("collector.test.dynamic", registry.GetSourceIds());
        Assert.Same(zone, registry.GetZone("collector.test.dynamic"));

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
            StrictHostManagerProfileLoader.ExpandMetricDefaults(buffer.ToArray()),
            "test/default.json");
        return new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(),
            planEpoch,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
    }
}
