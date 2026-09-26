using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resource_Manager_APP.Tests;

internal static class HostManagerTestPlanFactory
{
    public static CompiledHostManagerSmartCoordinatorPlan SmartCoordinator { get; }
        = CreateSmartCoordinator();

    public static CompiledHostManagerMemoryCleanupHotPublishPlan MemoryCleanup { get; }
        = CreatePlan().HotPublish.MemoryCleanup;

    public static AppSettingsUpdateResult CreateSettingsInput(
        AppPerformanceSettings? performance = null,
        AppLocalPublicServiceSettings? publicService = null)
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Performance = performance ?? defaults.Performance,
            PublicService = publicService ?? defaults.PublicService
        };
        var digest = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(settings)));
        return new AppSettingsUpdateResult(
            settings,
            DateTimeOffset.UnixEpoch,
            "test/settings.json",
            new AppSettingsSourceMetadata(
                AppSettingsSourceKind.TestFixture,
                settings.Version,
                digest,
                digest,
                RewritePerformed: false));
    }

    private static CompiledHostManagerSmartCoordinatorPlan CreateSmartCoordinator()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var loaded = StrictHostManagerProfileLoader.LoadBytes(buffer.ToArray(), "test/default.json");
        return new HostManagerPlanCompiler()
            .Compile(loaded, CreateSettingsInput(), 1, CreateCpuTopology(1), CreateCpuScoring(CreateCpuTopology(1)))
            .SmartCoordinator;
    }

    public static CompiledHostManagerPlan CreatePlan(
        Action<JsonObject>? editProfile = null,
        AppPerformanceSettings? performance = null,
        ulong planEpoch = 1,
        AppLocalPublicServiceSettings? publicService = null,
        Action<JsonObject>? editFreedomPoints = null,
        CpuTopologySnapshot? cpuTopology = null,
        CompiledCpuScoringPlan? cpuScoring = null)
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (editProfile is not null)
        {
            var root = JsonNode.Parse(bytes)?.AsObject()
                ?? throw new InvalidOperationException("Embedded Host Manager profile is invalid.");
            editProfile(root);
            bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        }
        var loaded = StrictHostManagerProfileLoader.LoadBytes(bytes, "test/default.json");
        var freedomPoints = editFreedomPoints is null ? null : FreedomPointTestFactory.Load(editFreedomPoints);
        return new HostManagerPlanCompiler()
            .Compile(
                loaded,
                CreateSettingsInput(performance, publicService),
                planEpoch,
                cpuTopology ?? CreateCpuTopology(1),
                cpuScoring ?? CreateCpuScoring(cpuTopology ?? CreateCpuTopology(1)),
                freedomPoints);
    }

    public static CompiledCpuScoringPlan CreateCpuScoring(CpuTopologySnapshot topology, double baselineRatio = 1)
        => CpuScoringPlanCompiler.Compile(topology,
            topology.PhysicalCores.ToDictionary(static core => core.Index, static _ => 1d), baselineRatio);

    public static CpuTopologySnapshot CreateCpuTopology(params int[] siblingsPerCore)
    {
        var physical = new List<CpuPhysicalCoreModel>();
        var logical = new List<CpuLogicalProcessorModel>();
        for (var coreIndex = 0; coreIndex < siblingsPerCore.Length; coreIndex++)
        {
            var logicalIds = Enumerable.Range(logical.Count, siblingsPerCore[coreIndex]).ToArray();
            physical.Add(new CpuPhysicalCoreModel($"core:{coreIndex}", coreIndex, $"Core {coreIndex}",
                "ccd:0", 0, 1, null, logicalIds, []));
            logical.AddRange(logicalIds.Select(id => new CpuLogicalProcessorModel(id, 0, id,
                $"core:{coreIndex}", "ccd:0", 1, null, true)));
        }
        return new CpuTopologySnapshot(
            DateTimeOffset.UnixEpoch,
            "Test CPU",
            new CpuSpecificationModel(
                "Test CPU", "Test", "Test", physical.Count, logical.Count, null, null, null, null, "fixture"),
            "fixture", "fixture", CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
            CpuTopologyVisualLayoutKinds.Grid, "fixture", physical.Count, logical.Count,
            1, siblingsPerCore.Any(count => count > 1),
            [new CpuCcdModel("ccd:0", 0, "CCD 0", null,
                physical.Select(core => core.Index).ToArray(), logical.Select(core => core.Id).ToArray(), "fixture")],
            physical, logical, []);
    }

    public static HostManagerSoftwareIdentityOwner CreateSoftwareIdentityOwner()
    {
        var hostPlan = CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "software-identity-test",
            HostManager = hostPlan
        });
        var owner = new HostManagerSoftwareIdentityOwner(
            provider,
            new HostManagerSoftwareIdentityRuntime(provider, deployment),
            NullLogger<HostManagerSoftwareIdentityOwner>.Instance);
        owner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return owner;
    }
}
