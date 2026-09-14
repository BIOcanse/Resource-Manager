using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementPolicyResolverTests
{
    [Fact]
    public void CreateSoftwarePolicy_DefaultsToAuto()
    {
        var policy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A");

        Assert.Equal(GpuPlacementPolicyModes.Auto, policy.EnabledMode);
        Assert.Equal(GpuPlacementSchedulingModes.Precise, policy.SchedulingMode);
        Assert.Equal(GpuPlacementTargets.SystemDefaultGpu, policy.StartupTargetGpu);
        Assert.Equal(GpuPlacementTargets.AutoIdleGpu, policy.TargetGpu);
        Assert.Equal(GpuPlacementRuntimeSchedulingModes.Precise, policy.RuntimeSchedulingMode);
        Assert.True(policy.RuntimeHotSwitchEnabled == true);
        Assert.Contains(GpuPlacementProviderIds.D3dDeviceCreateShim, policy.AllowedProviders);
        Assert.Equal(GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover, policy.PreferredRuntimeSwitchMethod);
        Assert.True(policy.KeepCpuProcessesOnMainCcd == true);
        Assert.False(policy.GpuExclusive);
        Assert.False(policy.AbsolutePerformanceModeEnabled);
        Assert.Equal(CpuMaximumOccupancyModes.SingleCcd, policy.CpuMaximumOccupancyMode);
        Assert.False(policy.CpuExclusiveLocksAffinity);
    }

    [Fact]
    public void CreateSoftwarePolicy_DisablesRuntimeHotSwitchForGamesAndHighPerformanceSoftware()
    {
        var game = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("game-a", "Game A", SoftwareKinds.Game);
        var highPerformance = GpuPlacementPolicyDefaults.CreateSoftwarePolicy(
            "prof-a",
            "Professional A",
            SoftwareKinds.HighPerformance);
        var ordinary = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("app-a", "App A", SoftwareKinds.Other);

        Assert.True(game.RuntimeHotSwitchEnabled == false);
        Assert.True(highPerformance.RuntimeHotSwitchEnabled == false);
        Assert.True(ordinary.RuntimeHotSwitchEnabled == true);
    }

    [Fact]
    public void AcceptsRuntimeGpuScheduling_RequiresSoftwareGateAndExecutableShimPolicy()
    {
        var enabled = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A", SoftwareKinds.Other));
        var gameDefault = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("game-a", "Game A", SoftwareKinds.Game),
            SoftwareKinds.Game);
        var disabledMode = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-b", "Software B", SoftwareKinds.Other) with
            {
                EnabledMode = GpuPlacementPolicyModes.Disabled
            });

        Assert.True(enabled.AcceptsRuntimeGpuScheduling());
        Assert.False(gameDefault.AcceptsRuntimeGpuScheduling());
        Assert.False(disabledMode.AcceptsRuntimeGpuScheduling());
    }

    [Fact]
    public void CreateProcessPolicy_DefaultsToSystemGpuTarget()
    {
        var policy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe"));

        Assert.Equal(GpuPlacementTargets.SystemDefaultGpu, policy.TargetGpu);
    }

    [Fact]
    public void NormalizeTarget_KeepsHighPerformanceAsCompatibilityTarget()
    {
        var target = GpuPlacementTargets.Normalize("highperformancegpu");

        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, target);
    }

    [Fact]
    public void NormalizeTarget_RejectsMalformedExactTarget()
    {
        Assert.Equal(GpuPlacementTargets.SystemDefaultGpu, GpuPlacementTargets.Normalize("GPUinvalid"));
        Assert.Equal("GPU12", GpuPlacementTargets.Normalize("gpu12"));
    }

    [Fact]
    public void NormalizeSystemTarget_RejectsPreciseAndSchedulingAutoTargets()
    {
        Assert.Equal(GpuPlacementTargets.SystemDefaultGpu, GpuPlacementTargets.NormalizeSystemTarget("GPU1"));
        Assert.Equal(GpuPlacementTargets.SystemDefaultGpu, GpuPlacementTargets.NormalizeSystemTarget(GpuPlacementTargets.AutoIdleGpu));
        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, GpuPlacementTargets.NormalizeSystemTarget(GpuPlacementTargets.HighPerformanceGpu));
        Assert.Equal(GpuPlacementTargets.IntegratedGpu, GpuPlacementTargets.NormalizeSystemTarget(GpuPlacementTargets.IntegratedGpu));
    }

    [Fact]
    public void NormalizeRuntimeSwitchMethod_DefaultsToFutureFrameTakeover()
    {
        Assert.Equal(
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            GpuPlacementRuntimeSwitchMethods.Normalize(null));
        Assert.Equal(
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            GpuPlacementRuntimeSwitchMethods.Normalize("unknown"));
        Assert.Equal(
            GpuPlacementRuntimeSwitchMethods.WindowRerender,
            GpuPlacementRuntimeSwitchMethods.Normalize(GpuPlacementRuntimeSwitchMethods.WindowRerender));
    }

    [Fact]
    public void Resolve_UsesSoftwarePolicyWhenProcessInherits()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto,
            MaxRisk = GpuPlacementRiskLevels.Medium,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = true,
            EnabledMode = GpuPlacementPolicyModes.Disabled
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.Equal(GpuPlacementPolicyModes.Auto, resolved.EnabledMode);
        Assert.True(resolved.AllowsRuntimeShimExecution());
        Assert.True(resolved.RuntimeHotSwitchEnabled);
        Assert.False(resolved.GpuExclusive);
    }

    [Fact]
    public void Resolve_UsesProcessPolicyWhenOverrideIsEnabled()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            EnabledMode = GpuPlacementPolicyModes.Preview,
            AllowedProviders = [GpuPlacementProviderIds.WindowsGraphicsPreference],
            SchedulingMode = GpuPlacementSchedulingModes.Precise,
            StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise,
            RuntimeHotSwitchEnabled = false,
            PreferredRuntimeSwitchMethod = GpuPlacementRuntimeSwitchMethods.WindowRerender,
            GpuExclusive = true
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = false,
            EnabledMode = GpuPlacementPolicyModes.Manual,
            MaxRisk = GpuPlacementRiskLevels.Medium,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.Equal(GpuPlacementPolicyModes.Manual, resolved.EnabledMode);
        Assert.Equal(GpuPlacementRiskLevels.Medium, resolved.MaxRisk);
        Assert.Equal(GpuPlacementSchedulingModes.Precise, resolved.SchedulingMode);
        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, resolved.StartupTargetGpu);
        Assert.Equal(GpuPlacementRuntimeSchedulingModes.Precise, resolved.RuntimeSchedulingMode);
        Assert.True(resolved.AllowsRuntimeShimExecution());
        Assert.False(resolved.RuntimeHotSwitchEnabled);
        Assert.Equal(GpuPlacementRuntimeSwitchMethods.WindowRerender, resolved.PreferredRuntimeSwitchMethod);
        Assert.True(resolved.KeepCpuProcessesOnMainCcd);
        Assert.True(resolved.GpuExclusive);
        Assert.False(resolved.AbsolutePerformanceModeEnabled);
        Assert.Equal(CpuMaximumOccupancyModes.SingleCcd, resolved.CpuMaximumOccupancyMode);
        Assert.False(resolved.CpuExclusiveLocksAffinity);
    }

    [Fact]
    public void Resolve_PreservesSoftwareCpuMaximumOccupancyModeAcrossProcessPolicy()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores,
            ProcessOverrideAllowed = true
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = false,
            EnabledMode = GpuPlacementPolicyModes.Manual
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.False(resolved.KeepCpuProcessesOnMainCcd);
        Assert.Equal(CpuMaximumOccupancyModes.AllCores, resolved.CpuMaximumOccupancyMode);
    }

    [Fact]
    public void Resolve_PreservesSoftwareAbsolutePerformanceSwitchAcrossProcessPolicy()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            AbsolutePerformanceModeEnabled = true,
            ProcessOverrideAllowed = true
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = false,
            EnabledMode = GpuPlacementPolicyModes.Manual
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.True(resolved.AbsolutePerformanceModeEnabled);
    }

    [Fact]
    public void Resolve_PreservesSoftwareCpuExclusiveLockSwitchAcrossProcessPolicy()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            CpuExclusiveLocksAffinity = true,
            ProcessOverrideAllowed = true
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = false,
            EnabledMode = GpuPlacementPolicyModes.Manual
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.True(resolved.CpuExclusiveLocksAffinity);
    }

    [Fact]
    public void Resolve_PreservesSoftwareManualCpuPlacementIdsAcrossProcessPolicy()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            CpuManualExclusivePositionIds = [" core:2 ", "ccd:1", "CORE:2"],
            CpuManualLockedPositionIds = ["ccd:0", " core:3 "],
            ProcessOverrideAllowed = true
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = false,
            EnabledMode = GpuPlacementPolicyModes.Manual
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.Equal(["ccd:1", "core:2"], resolved.CpuManualExclusivePositionIds);
        Assert.Equal(["ccd:0", "core:3"], resolved.CpuManualLockedPositionIds);
    }

    [Fact]
    public void AllowsRuntimeShimExecution_RejectsPreviewOrMissingD3dProvider()
    {
        var preview = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
            {
                EnabledMode = GpuPlacementPolicyModes.Preview
            });
        var autoWithoutProvider = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto,
                AllowedProviders = [GpuPlacementProviderIds.WindowsGraphicsPreference]
            });

        Assert.False(preview.AllowsRuntimeShimExecution());
        Assert.False(autoWithoutProvider.AllowsRuntimeShimExecution());
    }

    [Fact]
    public void AllowsRuntimeShimExecution_RejectsOrdinaryRuntimeCompatibilityMode()
    {
        var policy = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto,
                AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim],
                SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Ordinary
            });

        Assert.False(policy.AllowsRuntimeShimExecution());
    }

    private static GpuPlacementPolicyDocument Document(
        IReadOnlyList<GpuPlacementSoftwarePolicy>? softwarePolicies = null,
        IReadOnlyList<GpuPlacementProcessPolicy>? processPolicies = null)
    {
        return new GpuPlacementPolicyDocument(
            GpuPlacementPolicyDocumentVersions.Current,
            softwarePolicies ?? [],
            processPolicies ?? [],
            DateTimeOffset.UnixEpoch);
    }

    private static GpuPlacementObservedProcess Process(string processName, string executablePath)
    {
        return new GpuPlacementObservedProcess(
            OptimizationBaseScorePolicyResolver.CreateProcessKey(processName, executablePath),
            processName,
            executablePath,
            "x64",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            null,
            ["test"],
            100);
    }
}
