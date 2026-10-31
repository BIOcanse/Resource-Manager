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
    public void OverlayDefaultsAndTheirConsumerLimitComeFromTheDeclaredPoint()
    {
        var registry = FreedomPointTestFactory.Load(root =>
        {
            var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.PerformanceOverlay)["value"]!;
            value["default_font_size_px"] = 23;
            value["maximum_items"] = 5;
            value["default_items"] = JsonNode.Parse("""[{"kind":"software","id":"cpu.usage","source":"resource-breakdown","intervalMs":250,"syncPrecision":"high"}]""");
        });
        var compilation = registry.BeginCompilation(Profile(), new(1.25, CompiledCpuCoreWeights.Empty));
        var defaults = ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.Compile(compilation).PerformanceOverlay;
        var entry = defaults.Complete(new());
        Assert.Equal(23, entry.FontSizePx);
        Assert.Equal(5, defaults.MaximumItems);
        Assert.Equal(new ResourceManager.App.Domain.Settings.TargetedRecordingItem("software", "cpu.usage", "resource-breakdown", 250, "high"),
            Assert.Single(entry.Items!));
        Assert.Equal("performance-overlay-too-many-items", defaults.ValidateEntry(entry with { Items = Enumerable.Repeat(entry.Items![0], 6).ToArray() }));
    }

    [Fact]
    public void BackendGroupingDataNamesTheCurrentDeclarationContractAndGroupsTheReportPoints()
    {
        var registry = FreedomPointRegistry.LoadEmbedded().Sdk.Registry;
        var groups = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Core", "Configuration", "FreedomPoints",
            "backend.groups.json")))!;
        // The frontend generator refuses a stale grouping contract, so the declaration owner keeps it current.
        Assert.Equal(registry.ContractFingerprint, groups["contractFingerprint"]!.GetValue<string>());
        var monitoring = groups["groups"]!.AsArray().Single(group => group!["key"]!.GetValue<string>() == "resource-manager/backend/monitoring");
        Assert.Equal(["0001000200040001", "0001000200040002", "0001000200050000", "0001000200050001", "0001000200050002",
                "0001000200050003", "0001000200050004", "0001000200050005", "0001000200050006", "0001000200050007", "0001000200050008", "0001000200050009", "000100020005000A"],
            monitoring!["points"]!.AsArray().Select(point => point!.GetValue<string>()));
        var tiers = groups["groups"]!.AsArray().Single(group => group!["key"]!.GetValue<string>() == "resource-manager/backend/memory_cpu_tiers");
        Assert.Equal(Enumerable.Range(0, 12).Select(static index => $"00010002003500{index:X2}"),
            tiers!["points"]!.AsArray().Select(point => point!.GetValue<string>()));
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));

    [Fact]
    public void ActualCompiledMembersBindEveryActivePositionAndNoPendingPosition()
    {
        var host = FreedomPointRegistry.LoadEmbedded();
        var registry = host.Sdk.Registry;
        var members = typeof(CompiledHostManagerPlan).Assembly.GetTypes()
            .Where(static type => type.Namespace == "ResourceManager.App.Domain.RuntimeSpecialization")
            .SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(static member => member.GetCustomAttributes<FreedomPointBindingAttribute>().Select(attribute => (Member: member, Path: PointPath.FromRaw(attribute.Path))))
            .Concat(typeof(ResourceManager.App.Infrastructure.Telemetry.Etw.NativeKernelEtwSession).Assembly
                .GetType("ResourceManager.App.Infrastructure.Telemetry.Etw.KernelEtwNativeDefaults", throwOnError: true)!
                .GetProperties(BindingFlags.NonPublic | BindingFlags.Static)
                .SelectMany(static member => member.GetCustomAttributes<FreedomPointBindingAttribute>()
                    .Select(attribute => (Member: member, Path: PointPath.FromRaw(attribute.Path)))))
            .Concat(typeof(ResourceManager.App.Infrastructure.Telemetry.Etw.NativeKernelEtwSession).Assembly
                .GetType("ResourceManager.App.Infrastructure.Telemetry.Etw.NativePolicyDefaults", throwOnError: true)!
                .GetProperties(BindingFlags.NonPublic | BindingFlags.Static)
                .SelectMany(static member => member.GetCustomAttributes<FreedomPointBindingAttribute>()
                    .Select(attribute => (Member: member, Path: PointPath.FromRaw(attribute.Path)))))
            .ToArray();
        // Kernel ETW and native policy each have one managed SDK projection in addition to their plan binding.
        Assert.Equal(80, members.Length);
        Assert.Equal(78, members.Select(static m => m.Path).Distinct().Count());
        Assert.All(registry.Points, point =>
        {
            if (point.Status == DeclarationStatus.Active)
            {
                var bound = members.Where(member => member.Path == point.Path).ToArray();
                Assert.Equal(point.GenerationTargets.Length, bound.Length);
                foreach (var target in point.GenerationTargets)
                {
                    var member = Assert.Single(bound, member => member.Member.DeclaringType!.FullName == target.Scope && member.Member.Name == target.Symbol).Member;
                    Assert.Equal(GenerationLanguage.HostArtifact, target.Language);
                    Assert.Equal(point.Path.Family == PointFamily.Build ? GenerationForm.BuildConstant : GenerationForm.RuntimeImmutable, target.Form);
                    Assert.Equal(member.DeclaringType!.FullName, target.Scope);
                    Assert.Equal(member.Name, target.Symbol);
                }
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
        // Includes build-selected optimization, persistence and native policy declarations.
        Assert.Equal(78, compilation.Facts.Items.Length);
        // The native kernel owner adds one active compiled limits declaration.
        _ = compilation.Facts.Require(host.Sdk.RequirePath(BackendFreedomPointPaths.KernelEtwLimits));
        var availability = host.Sdk.RequirePath(BackendFreedomPointPaths.HardwareBridgeAvailabilityCheckInterval);
        Assert.Equal(30000L, compilation.Facts.Require(availability).Value.Slots[0].Integer);
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
