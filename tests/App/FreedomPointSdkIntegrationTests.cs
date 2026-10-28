using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreedomPoint;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed class FreedomPointSdkIntegrationTests
{
    [Fact]
    public void ActualCompiledMembersBindEveryActivePositionAndNoPendingPosition()
    {
        var host = FreedomPointRegistry.LoadEmbedded();
        var registry = host.Sdk.Registry;
        var members = typeof(CompiledHostManagerPlan).Assembly.GetTypes()
            .Where(static type => type.Namespace == "ResourceManager.App.Domain.RuntimeSpecialization")
            .SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(static member => member.GetCustomAttributes<FreedomPointBindingAttribute>().Select(attribute => (Member: member, Path: PointPath.FromRaw(attribute.Path))))
            .ToArray();
        Assert.Equal(26, members.Length);
        Assert.Equal(26, members.Select(static m => m.Path).Distinct().Count());
        Assert.All(registry.Points, point =>
        {
            if (point.Status == DeclarationStatus.Active)
            {
                var member = Assert.Single(members, member => member.Path == point.Path).Member;
                var target = Assert.Single(point.GenerationTargets);
                Assert.Equal(GenerationLanguage.HostArtifact, target.Language);
                Assert.Equal(point.Path.Family == PointFamily.Build ? GenerationForm.BuildConstant : GenerationForm.RuntimeImmutable, target.Form);
                Assert.Equal(member.DeclaringType!.FullName, target.Scope);
                Assert.Equal(member.Name, target.Symbol);
            }
            else
            {
                Assert.DoesNotContain(members, member => member.Path == point.Path);
                Assert.Empty(point.GenerationTargets);
                Assert.Null(point.Update);
                Assert.Null(point.ValueSource);
            }
        });
        Assert.Equal(registry.ContractFingerprint, HostManagerTestPlanFactory.CreatePlan().FreedomPoints.SdkContractFingerprint);
        Assert.All(host.Declarations.EnumeratePoints(), point => Assert.Equal(point.Path, host.Sdk.RequirePath(point.Address)));
    }

    [Fact]
    public void PreviewUsesActualHostFactsAndRejectsAChangedApplicationBase()
    {
        var host = FreedomPointRegistry.LoadEmbedded();
        var before = host.BeginCompilation(Profile(), new(1.25, CompiledCpuCoreWeights.Empty)).Facts;
        var candidate = host.BeginCompilation(Profile(), new(1.5, CompiledCpuCoreWeights.Empty)).Facts;
        var preview = new ConfigurationChange(host.Sdk.Registry, before, candidate);
        var change = Assert.Single(preview.Points);
        Assert.Equal(host.Sdk.RequirePath(BackendFreedomPointPaths.CpuBaselineRatio), change.Point);
        Assert.Equal(1.25, change.Before.Slots[0].Decimal);
        Assert.Equal(1.5, change.After.Slots[0].Decimal);
        Assert.NotEmpty(preview.Targets);
        preview.ValidateBase(host.Sdk.Registry, before, candidate);
        Assert.Equal("stale_configuration_preview", Assert.Throws<FreedomPointException>(
            () => preview.ValidateBase(host.Sdk.Registry, candidate, candidate)).Code);
    }

    [Fact]
    public void CompleteFactsComeFromTheSameSourcesBeforeAnyConsumerRuns()
    {
        var host = FreedomPointRegistry.LoadEmbedded();
        var cpu = new CompiledCpuScoringPlan(1.25, CompiledCpuCoreWeights.Empty);
        var compilation = host.BeginCompilation(Profile(), cpu);
        Assert.Equal(26, compilation.Facts.Items.Length);
        var ratio = host.Sdk.RequirePath(BackendFreedomPointPaths.CpuBaselineRatio);
        Assert.Equal(1.25, compilation.Facts.Require(ratio).Value.Slots[0].Decimal);
        Assert.Equal(1.25, compilation.Consume<double>(BackendFreedomPointPaths.CpuBaselineRatio, "HostManagerPlanCompiler.CompileCpuScoring"));
        Assert.Single(host.Sdk.Registry.FindSource("cpu_configuration", "/baseline_ratio"), point => point.Path == ratio);
        var observation = host.Sdk.RequirePath(BackendFreedomPointPaths.CpuResidencyObservationWindow);
        Assert.Equal(5000L, compilation.Facts.Require(observation).Value.Slots[0].Integer);
        Assert.Throws<InvalidDataException>(() => host.BeginCompilation(Profile()));
    }

    [Fact]
    public void DisplayNamesAndCustomViewsDoNotOwnPositions()
    {
        var before = FreedomPointRegistry.LoadEmbedded();
        var renamed = FreedomPointTestFactory.Load(root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyObservationWindow)["id"] = "renamed_observation");
        var path = before.Sdk.RequirePath(BackendFreedomPointPaths.CpuResidencyObservationWindow);
        Assert.Equal(path, renamed.Sdk.RequirePath(BackendFreedomPointPaths.CpuResidencyObservationWindow));
        var partition = new CustomPartition(before.Sdk.Registry, before.Sdk.Registry.ContractFingerprint,
            [new("custom", null, "CPU by function", null, 0, [path])]);
        var moved = new CustomPartition(before.Sdk.Registry, before.Sdk.Registry.ContractFingerprint,
            [new("other", null, "Moved display group", null, 3, [path])]);
        Assert.Equal(partition.EnumeratePointsUnder("custom").ToArray(), moved.EnumeratePointsUnder("other").ToArray());
        Assert.Equal(before.Sdk.Registry.ContractFingerprint, moved.ContractFingerprint);
    }

    [Fact]
    public void ChangingDeclarationOrderRejectsOldViewContract()
    {
        var before = FreedomPointRegistry.LoadEmbedded();
        var moved = FreedomPointTestFactory.Load(root =>
        {
            var children = root["children"]![0]!["children"]!.AsArray();
            var monitoring = children[0]!.DeepClone();
            children.RemoveAt(0);
            children.Add(monitoring);
        }, FreedomPointTestFactory.FixtureTargets);
        Assert.NotEqual(before.Sdk.RequirePath(BackendFreedomPointPaths.CpuResidencyObservationWindow), moved.Sdk.RequirePath(BackendFreedomPointPaths.CpuResidencyObservationWindow));
        var error = Assert.Throws<FreedomPointException>(() => new CustomPartition(moved.Sdk.Registry, before.Sdk.Registry.ContractFingerprint, []));
        Assert.Equal("partition_contract_mismatch", error.Code);
    }

    [Fact]
    public void HexPositionsRemainExactInProductJson()
    {
        var point = FreedomPointRegistry.LoadEmbedded().Declarations.EnumeratePoints().First();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(point));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("Path").ValueKind);
        Assert.Equal(point.Path.ToString(), json.RootElement.GetProperty("Path").GetString());
    }

    private static HostManagerConfigurationProfile Profile()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return StrictHostManagerProfileLoader.LoadBytes(StrictHostManagerProfileLoader.ExpandMetricDefaults(bytes.ToArray()), "test/default.json").Profile;
    }
}
