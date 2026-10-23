using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuRuntimeTargetPersistenceTests
{
    [Theory]
    [InlineData("IntegratedGpu")]
    [InlineData("GPU1")]
    public async Task OldRuntimeTargetIsDiscardedWithoutChangingStartupPreference(string oldTarget)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-gpu-policy-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var original = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
            { StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu };
            var document = new GpuPlacementPolicyDocument("1.2.0", [original], [], DateTimeOffset.UnixEpoch);
            var json = JsonSerializer.SerializeToNode(document, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            json["softwarePolicies"]![0]!["targetGpu"] = oldTarget;
            var path = Path.Combine(root, "UserData", "SoftwareProfiles", "gpu-placement-policies.local.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json.ToJsonString());
            var store = new JsonGpuPlacementPolicyStore(new Environment(root), NullLogger<JsonGpuPlacementPolicyStore>.Instance);
            var loaded = Assert.Single((await store.GetAsync(CancellationToken.None)).SoftwarePolicies);
            Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, loaded.StartupTargetGpu);
            await store.SaveSoftwarePolicyAsync(loaded, CancellationToken.None);
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.False(saved["softwarePolicies"]![0]!.AsObject().ContainsKey("targetGpu"));
            Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, saved["softwarePolicies"]![0]!["startupTargetGpu"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
