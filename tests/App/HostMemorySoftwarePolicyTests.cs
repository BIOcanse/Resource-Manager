using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class HostMemorySoftwarePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(SoftwareKinds.Game)]
    [InlineData(SoftwareKinds.HighPerformance)]
    public async Task NewSoftwareAllowsDumpByDefault(string? softwareKind)
    {
        using var root = new OwnedRoot();

        Assert.False(GpuPlacementPolicyDefaults.CreateSoftwarePolicy("app", "App", softwareKind)
            .HostMemoryProtectionNoDump);
        var policy = await root.CreateStore().GetOrCreateSoftwarePolicyAsync(
            "app", "App", softwareKind, CancellationToken.None);

        Assert.False(policy.HostMemoryProtectionNoDump);
        Assert.False(File.Exists(root.PolicyPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoDumpPersistsBySoftwareAcrossSaveAndReload(bool noDump)
    {
        using var root = new OwnedRoot();
        var store = root.CreateStore();
        var request = GpuPlacementPolicyDefaults.CreateSoftwarePolicy(" app ", " App ") with
        {
            HostMemoryProtectionNoDump = noDump,
            CpuSchedulingExcluded = true,
            GpuSchedulingExcluded = true,
            RuntimeHotSwitchEnabled = null
        };

        var saved = await store.SaveSoftwarePolicyAsync(request, CancellationToken.None);
        Assert.Equal("app", saved.SoftwareId);
        Assert.Equal(noDump, saved.HostMemoryProtectionNoDump);
        await store.SaveSoftwarePolicyAsync(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("other", "Other"), CancellationToken.None);

        var reloadedStore = root.CreateStore();
        var reloaded = await reloadedStore.GetOrCreateSoftwarePolicyAsync(
            "APP", "App", SoftwareKinds.Game, CancellationToken.None);
        Assert.Equal(noDump, reloaded.HostMemoryProtectionNoDump);
        Assert.True(reloaded.CpuSchedulingExcluded);
        Assert.False(reloaded.MemorySchedulingExcluded);
        Assert.True(reloaded.GpuSchedulingExcluded);
        var document = await reloadedStore.GetAsync(CancellationToken.None);
        Assert.False(document.SoftwarePolicies.Single(policy => policy.SoftwareId == "other")
            .HostMemoryProtectionNoDump);
        var wirePolicy = root.ReadPolicyNode()["softwarePolicies"]!.AsArray()
            .Single(policy => policy!["softwareId"]!.GetValue<string>() == "app")!;
        Assert.Equal(noDump, wirePolicy["hostMemoryProtectionNoDump"]!.GetValue<bool>());

        await reloadedStore.SaveSoftwarePolicyAsync(reloaded with
        {
            HostMemoryProtectionNoDump = !noDump
        }, CancellationToken.None);
        Assert.Equal(!noDump, (await root.CreateStore().GetOrCreateSoftwarePolicyAsync(
            "app", "App", null, CancellationToken.None)).HostMemoryProtectionNoDump);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.3.0")]
    [InlineData("2.0.0")]
    public async Task ExistingPolicyWithoutFlagAllowsDump(string? version)
    {
        using var root = new OwnedRoot();
        var document = CreateDocument(version);
        document["softwarePolicies"]![0]!.AsObject().Remove("hostMemoryProtectionNoDump");
        root.WritePolicyFile(document);

        var policy = Assert.Single((await root.CreateStore().GetAsync(CancellationToken.None)).SoftwarePolicies);

        Assert.False(policy.HostMemoryProtectionNoDump);
        Assert.False((await root.CreateStore().GetOrCreateSoftwarePolicyAsync(
            "app", "App", null, CancellationToken.None)).HostMemoryProtectionNoDump);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.3.0")]
    [InlineData("2.0.0")]
    public async Task MigrationAndStoredWireRewritePreserveNoDump(string? version)
    {
        using var root = new OwnedRoot();
        var document = CreateDocument(version);
        var wirePolicy = document["softwarePolicies"]![0]!;
        wirePolicy["hostMemoryProtectionNoDump"] = true;
        wirePolicy["startupTargetGpu"] = "HighPerformanceGpu";
        root.WritePolicyFile(document);

        var policy = Assert.Single((await root.CreateStore().GetAsync(CancellationToken.None)).SoftwarePolicies);

        Assert.True(policy.HostMemoryProtectionNoDump);
        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, policy.StartupTargetGpu);
        var rewritten = root.ReadPolicyNode();
        Assert.Equal(GpuPlacementPolicyDocumentVersions.Current, rewritten["version"]!.GetValue<string>());
        Assert.True(rewritten["softwarePolicies"]![0]!["hostMemoryProtectionNoDump"]!.GetValue<bool>());
        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu,
            rewritten["softwarePolicies"]![0]!["startupTargetGpu"]!.GetValue<string>());
        Assert.True(Assert.Single((await root.CreateStore().GetAsync(CancellationToken.None)).SoftwarePolicies)
            .HostMemoryProtectionNoDump);
    }

    private static JsonObject CreateDocument(string? version)
    {
        var policy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("app", "App");
        var document = JsonSerializer.SerializeToNode(new GpuPlacementPolicyDocument(
            GpuPlacementPolicyDocumentVersions.Current, [policy], [], DateTimeOffset.MinValue),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (version is null) document.Remove("version");
        else document["version"] = version;
        return document;
    }

    private sealed class OwnedRoot : IDisposable
    {
        private readonly string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "rm-host-memory-software-policy-" + Guid.NewGuid().ToString("N"))).FullName;

        public string PolicyPath => Path.Combine(root, "UserData", "SoftwareProfiles", "gpu-placement-policies.local.json");

        public JsonGpuPlacementPolicyStore CreateStore() => new(
            new TestEnvironment(root), NullLogger<JsonGpuPlacementPolicyStore>.Instance);

        public void WritePolicyFile(JsonObject document)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
            File.WriteAllText(PolicyPath, document.ToJsonString());
        }

        public JsonObject ReadPolicyNode() => JsonNode.Parse(File.ReadAllText(PolicyPath))!.AsObject();

        public void Dispose()
        {
            var profiles = Path.GetDirectoryName(PolicyPath)!;
            if (Directory.Exists(profiles))
            {
                foreach (var file in Directory.EnumerateFiles(profiles)) File.Delete(file);
                Directory.Delete(profiles);
                Directory.Delete(Path.Combine(root, "UserData"));
            }
            Directory.Delete(root);
        }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
