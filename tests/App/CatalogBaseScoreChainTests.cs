using System.Text.Json;
using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

public sealed class CatalogBaseScoreChainTests
{
    [Theory]
    [InlineData(0, 42, "type_default")]
    [InlineData(1, 55, "catalog_group")]
    [InlineData(2, 30, "catalog_process")]
    [InlineData(3, 73, "user_software")]
    [InlineData(4, 88, "user_process")]
    public void EveryLayerOverridesEarlierLayersAndRetainsTheirValues(int depth, double expected, string source)
    {
        var catalog = Catalog(depth);
        var user = UserPolicy(depth);
        var plan = OptimizationBaseScorePolicyResolver.Compile(user, catalog);
        var key = OptimizationBaseScorePolicyResolver.CreateProcessKey("helper", Path);
        var result = plan.Resolve("software", SoftwareKinds.Managed, key, "helper", Path);
        Assert.Equal(expected, result.Value);
        Assert.Equal(source, result.Source);
        Assert.Equal(new[] { "type_default", "catalog_group", "catalog_process", "user_software", "user_process" },
            result.Layers.Select(layer => layer.Source));
        Assert.Equal(new double?[] { 42, depth >= 1 ? 55 : null, depth >= 2 ? 30 : null,
            depth >= 3 ? 73 : null, depth >= 4 ? 88 : null }, result.Layers.Select(layer => layer.Value));
        Assert.Equal(expected, plan.ResolveBaseScore("software", SoftwareKinds.Managed, key, "helper", Path));
        Assert.Equal(expected, new OptimizationBaseScorePolicyResolver(user, catalog)
            .ResolveBaseScore("software", SoftwareKinds.Managed, "helper", Path));
        // Scheduling attribution projects the same result as the detail/report reader.
        var fact = WindowsResourceBreakdownSampler.ProjectSchedulingAttribution(
            Fact(), new("software", "Software", SoftwareKinds.Managed, "managed", []), plan, key);
        Assert.Equal(expected, fact.BaseScore);
    }

    [Fact]
    public void ExactPathWinsOverNameAndWrongPathFallsBackToName()
    {
        var catalog = Catalog(2) with
        {
            ProcessAssignments = [
                new("name", "helper", null, null, null, 35),
                new("path", "helper", Path, null, null, 91)
            ]
        };
        var plan = OptimizationBaseScorePolicyResolver.Compile(GpuPlacementPolicyDocument.Empty, catalog);
        Assert.Equal(91, plan.ResolveBaseScore("software", SoftwareKinds.Managed, null, "helper.exe", Path));
        Assert.Equal(35, plan.ResolveBaseScore("software", SoftwareKinds.Managed, null, "helper", @"C:\Other\helper.exe"));
        Assert.Equal(55, plan.ResolveBaseScore("software", SoftwareKinds.Managed, null, "another", Path.Replace("helper", "another")));
    }

    [Fact]
    public async Task BundledCatalogSuppliesRmAndCriticalAndBackgroundServices()
    {
        var catalog = await new JsonSoftwarePolicyProfileProvider().GetProfileAsync(CancellationToken.None);
        var plan = OptimizationBaseScorePolicyResolver.Compile(GpuPlacementPolicyDocument.Empty, catalog);
        var rm = plan.Resolve(RuntimeAttributionIds.ResourceManagerSelf, SoftwareKinds.Adapted);
        Assert.Equal(90, rm.Value);
        Assert.Equal("catalog_group", rm.Source);
        Assert.Equal("adapted.resource-manager", rm.Layers[1].Key);
        Assert.Equal(30, plan.ResolveBaseScore(WindowsServiceSoftwareIds.ForService("DiagTrack"), SoftwareKinds.WindowsService, null));
        Assert.Equal(90, plan.ResolveBaseScore(WindowsServiceSoftwareIds.ForService("Audiosrv"), SoftwareKinds.WindowsService, null));
        // A shared host takes the highest matching service score, even beyond the eight names encoded in its ID.
        string[] services = ["a", "b", "c", "d", "e", "f", "g", "h", "DiagTrack", "Audiosrv"];
        var attribution = new RuntimeSoftwareAttribution(WindowsServiceSoftwareIds.ForServiceGroup(services),
            "", SoftwareKinds.WindowsService, SoftwareDisplayKinds.WindowsService, []) { ServiceNames = services };
        var projected = WindowsResourceBreakdownSampler.ProjectSchedulingAttribution(Fact(), attribution, plan, null);
        Assert.Equal(90, projected.BaseScore);
        Assert.Equal(services, projected.ServiceNames);
        Assert.Equal(30, plan.ResolveBaseScore(attribution.Id, attribution.Kind, null, "svchost", null, ["DiagTrack"]));
    }

    [Theory]
    [InlineData(SoftwareKinds.WindowsSystem)]
    [InlineData(SoftwareKinds.WindowsService)]
    [InlineData(SoftwareKinds.WindowsComponent)]
    public void WindowsTypeDefaultsAreMiddleTierAndExistingActionExclusionsRemain(string kind)
    {
        var result = CompiledBaseScorePlan.Empty.Resolve("fixture", kind);
        Assert.Equal(60, result.Value);
        Assert.Equal("type_default", result.Source);
        Assert.False(ResourceManager.App.Domain.Optimization.HostManagerTargetCapabilityClassifier.CanWriteProcessPolicy("fixture", kind));
        Assert.False(ResourceManager.App.Domain.Optimization.HostManagerTargetCapabilityClassifier.CanWritePhysicalPlacement("fixture", kind));
    }

    [Fact]
    public void DisabledOrInheritedProcessOverrideLeavesCatalogOrSoftwareEffective()
    {
        var user = UserPolicy(4);
        var disabled = user with { SoftwarePolicies = [user.SoftwarePolicies[0] with { ProcessOverrideAllowed = false }] };
        var inherited = user with { ProcessPolicies = [user.ProcessPolicies[0] with { Inherit = true }] };
        foreach (var document in new[] { disabled, inherited })
            Assert.Equal(73, new OptimizationBaseScorePolicyResolver(document, Catalog(2))
                .ResolveBaseScore("software", SoftwareKinds.Managed, "helper", Path));
    }

    [Fact]
    public void DetailJsonCarriesEffectiveValueAndAllSourceLayers()
    {
        var score = OptimizationBaseScorePolicyResolver.Compile(UserPolicy(4), Catalog(2))
            .Resolve("software", SoftwareKinds.Managed, OptimizationBaseScorePolicyResolver.CreateProcessKey("helper", Path), "helper", Path);
        var snapshot = new GpuPlacementSoftwareSettingsSnapshot("software", "Software",
            UserPolicy(4).SoftwarePolicies[0], [], GpuPlacementSoftwareProcessHistory.Empty("software", "Software"))
        { BaseScore = score, ProcessBaseScores = new Dictionary<string, BaseScoreResolution> { ["process"] = score } };
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(88, parsed.RootElement.GetProperty("baseScore").GetProperty("value").GetDouble());
        Assert.Equal("user_process", parsed.RootElement.GetProperty("baseScore").GetProperty("source").GetString());
        Assert.Equal(5, parsed.RootElement.GetProperty("processBaseScores").GetProperty("process").GetProperty("layers").GetArrayLength());
    }

    private const string Path = @"C:\Apps\helper.exe";
    private static SchedulingProcessFact Fact() => new(42, 123, "helper", Path, "", "", "", "", 0,
        SchedulingProcessMetricMask.None, 0, 0, 1, []);
    private static SoftwarePolicyProfile Catalog(int depth) => new(
        new Dictionary<string, SoftwarePolicyGroup> { ["group"] = new("group", "Group", null, []) { BaseScore = 55 } },
        depth >= 1 ? new Dictionary<string, SoftwarePolicyAssignment> { ["software"] = new("software", "Software", "group", []) } : new Dictionary<string, SoftwarePolicyAssignment>())
    { ProcessAssignments = depth >= 2 ? [new("path", "helper", Path, null, null, 30)] : [] };
    private static GpuPlacementPolicyDocument UserPolicy(int depth) => new(GpuPlacementPolicyDocumentVersions.Current,
        depth >= 3 ? [GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
            { BaseScoreOverride = 73, ProcessOverrideAllowed = true }] : [],
        depth >= 4 ? [new("software", OptimizationBaseScorePolicyResolver.CreateProcessKey("helper", Path),
            "helper", Path, false, GpuPlacementProviderIds.Defaults, 88, DateTimeOffset.UnixEpoch)] : [], DateTimeOffset.UnixEpoch);
}
