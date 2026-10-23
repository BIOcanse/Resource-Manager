using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementPolicyResolverTests
{
    [Theory]
    [InlineData(null, CpuMaximumOccupancyModes.AllCores)]
    [InlineData("", CpuMaximumOccupancyModes.AllCores)]
    [InlineData("unknown", CpuMaximumOccupancyModes.AllCores)]
    [InlineData(CpuMaximumOccupancyModes.AllCores, CpuMaximumOccupancyModes.AllCores)]
    [InlineData(CpuMaximumOccupancyModes.SingleCcd, CpuMaximumOccupancyModes.SingleCcd)]
    public void CpuOccupancyDefaultsToUnboundedAndPreservesExplicitSingleCcd(string? value, string expected)
    {
        Assert.Equal(expected, CpuMaximumOccupancyModes.Normalize(value));
        var policy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        { CpuMaximumOccupancyMode = value! };
        var resolved = ResolvedGpuPlacementPolicy.FromSoftware(policy);
        Assert.Equal(expected, resolved.CpuMaximumOccupancyMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPersistedOccupancyUsesAllCoresWhileExplicitSingleCcdSurvives(bool explicitSingle)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var policy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.SingleCcd };
        var json = System.Text.Json.JsonSerializer.SerializeToNode(policy, options)!.AsObject();
        if (!explicitSingle) json.Remove("cpuMaximumOccupancyMode");
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<GpuPlacementSoftwarePolicy>(json.ToJsonString(), options)!;
        Assert.Equal(explicitSingle ? CpuMaximumOccupancyModes.SingleCcd : CpuMaximumOccupancyModes.AllCores,
            reloaded.CpuMaximumOccupancyMode);
    }

    [Fact]
    public void CreateSoftwarePolicy_ParticipatesInEveryDomainAndWritesTheExclusionsExplicitly()
    {
        var policy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A");

        Assert.False(policy.CpuSchedulingExcluded);
        Assert.False(policy.MemorySchedulingExcluded);
        Assert.False(policy.GpuSchedulingExcluded);
        Assert.Equal(GpuPlacementTargets.SystemDefaultGpu, policy.StartupTargetGpu);
        Assert.Null(typeof(GpuPlacementSoftwarePolicy).GetProperty("TargetGpu"));
        Assert.True(policy.RuntimeHotSwitchEnabled == true);
        Assert.Contains(GpuPlacementProviderIds.D3dDeviceCreateShim, policy.AllowedProviders);
        Assert.Equal(GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover, policy.PreferredRuntimeSwitchMethod);
        Assert.Equal(CpuMaximumOccupancyModes.AllCores, policy.CpuMaximumOccupancyMode);
        foreach (var removed in new[]
        {
            "EnabledMode", "SchedulingMode", "RuntimeSchedulingMode", "ExplicitSelectionMode",
            "KeepCpuProcessesOnMainCcd", "GpuExclusive", "AbsolutePerformanceModeEnabled", "CpuExclusiveLocksAffinity"
        })
        {
            Assert.Null(typeof(GpuPlacementSoftwarePolicy).GetProperty(removed));
        }

        var json = System.Text.Json.JsonSerializer.SerializeToNode(
            policy,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();
        Assert.False(json["cpuSchedulingExcluded"]!.GetValue<bool>());
        Assert.False(json["memorySchedulingExcluded"]!.GetValue<bool>());
        Assert.False(json["gpuSchedulingExcluded"]!.GetValue<bool>());
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
                GpuSchedulingExcluded = true
            });

        Assert.True(enabled.AcceptsRuntimeGpuScheduling());
        Assert.False(gameDefault.AcceptsRuntimeGpuScheduling());
        Assert.False(disabledMode.AcceptsRuntimeGpuScheduling());
    }

    [Fact]
    public void ExternalRuntimePermissionDoesNotBorrowLegacyShimProviders()
    {
        var policy = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
            {
                AllowedProviders = [GpuPlacementProviderIds.WindowsGraphicsPreference]
            });

        Assert.False(policy.AcceptsRuntimeGpuScheduling());
        Assert.True(policy.AcceptsExternalRuntimeGpuScheduling());
        Assert.False((policy with { RuntimeHotSwitchEnabled = false }).AcceptsExternalRuntimeGpuScheduling());
        Assert.False((policy with { GpuSchedulingExcluded = true }).AcceptsExternalRuntimeGpuScheduling());
    }

    [Fact]
    public void CreateProcessPolicy_DefaultsToSystemGpuTarget()
    {
        var policy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe"));

        Assert.Null(typeof(GpuPlacementProcessPolicy).GetProperty("TargetGpu"));
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
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = true,
            AllowedProviders = []
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.False(resolved.GpuSchedulingExcluded);
        Assert.True(resolved.AllowsRuntimeShimExecution());
        Assert.True(resolved.RuntimeHotSwitchEnabled);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void ProcessOverrideInheritsSoftwareSchedulingParticipationUnchanged(
        bool cpuExcluded,
        bool memoryExcluded,
        bool gpuExcluded)
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            ProcessOverrideAllowed = true,
            CpuSchedulingExcluded = cpuExcluded,
            MemorySchedulingExcluded = memoryExcluded,
            GpuSchedulingExcluded = gpuExcluded
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("renderer", "C:\\Apps\\SoftwareA\\renderer.exe")) with
        {
            Inherit = false,
            BaseScoreOverride = 42
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other,
            "renderer", "C:\\Apps\\SoftwareA\\renderer.exe");

        Assert.Equal(cpuExcluded, resolved.CpuSchedulingExcluded);
        Assert.Equal(memoryExcluded, resolved.MemorySchedulingExcluded);
        Assert.Equal(gpuExcluded, resolved.GpuSchedulingExcluded);
        Assert.Equal(softwarePolicy.StartupTargetGpu, resolved.StartupTargetGpu);
        Assert.Equal(!gpuExcluded, resolved.AcceptsExternalRuntimeGpuScheduling());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GpuExclusionClosesEveryGpuPermissionWhileCpuAndMemoryExclusionsLeaveThemOpen(
        bool cpuExcluded,
        bool memoryExcluded)
    {
        var participating = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A", SoftwareKinds.Other) with
            {
                CpuSchedulingExcluded = cpuExcluded,
                MemorySchedulingExcluded = memoryExcluded
            });
        var excluded = participating with { GpuSchedulingExcluded = true };

        Assert.True(participating.AllowsRuntimeShimExecution());
        Assert.True(participating.AcceptsRuntimeGpuScheduling());
        Assert.True(participating.AcceptsExternalRuntimeGpuScheduling());
        Assert.True(participating.AllowsStartupShimExecution());
        Assert.NotEmpty(participating.GetStartupProviders(GpuGraphicsApi.D3D11));
        Assert.False(excluded.AllowsRuntimeShimExecution());
        Assert.False(excluded.AcceptsRuntimeGpuScheduling());
        Assert.False(excluded.AcceptsExternalRuntimeGpuScheduling());
        Assert.False(excluded.AllowsStartupShimExecution());
        Assert.Empty(excluded.GetStartupProviders(GpuGraphicsApi.D3D11));
        Assert.Null(excluded.GetRuntimeProvider(GpuGraphicsApi.D3D11));
    }

    [Fact]
    public void Resolve_UsesProcessPolicyWhenOverrideIsEnabled()
    {
        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
        {
            AllowedProviders = [GpuPlacementProviderIds.WindowsGraphicsPreference],
            StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu,
            RuntimeHotSwitchEnabled = false,
            PreferredRuntimeSwitchMethod = GpuPlacementRuntimeSwitchMethods.WindowRerender,
            CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.SingleCcd
        };
        var processPolicy = GpuPlacementPolicyDefaults.CreateProcessPolicy(
            "software-a",
            Process("helper", "C:\\Apps\\SoftwareA\\helper.exe")) with
        {
            Inherit = false,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, resolved.StartupTargetGpu);
        Assert.True(resolved.AllowsRuntimeShimExecution());
        Assert.False(resolved.RuntimeHotSwitchEnabled);
        Assert.Equal(GpuPlacementRuntimeSwitchMethods.WindowRerender, resolved.PreferredRuntimeSwitchMethod);
        Assert.Equal(CpuMaximumOccupancyModes.SingleCcd, resolved.CpuMaximumOccupancyMode);
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
            Inherit = false
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.Equal(CpuMaximumOccupancyModes.AllCores, resolved.CpuMaximumOccupancyMode);
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
            Inherit = false
        };
        var resolver = new GpuPlacementPolicyResolver(Document([softwarePolicy], [processPolicy]));

        var resolved = resolver.Resolve("software-a", "Software A", SoftwareKinds.Other, "helper", "C:\\Apps\\SoftwareA\\helper.exe");

        Assert.Equal(["ccd:1", "core:2"], resolved.CpuManualExclusivePositionIds);
        Assert.Equal(["ccd:0", "core:3"], resolved.CpuManualLockedPositionIds);
    }

    [Fact]
    public void AllowsRuntimeShimExecution_RejectsGpuExclusionOrMissingD3dProvider()
    {
        var excluded = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
            {
                GpuSchedulingExcluded = true
            });
        var autoWithoutProvider = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software-a", "Software A") with
            {
                AllowedProviders = [GpuPlacementProviderIds.WindowsGraphicsPreference]
            });

        Assert.False(excluded.AllowsRuntimeShimExecution());
        Assert.False(autoWithoutProvider.AllowsRuntimeShimExecution());
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
