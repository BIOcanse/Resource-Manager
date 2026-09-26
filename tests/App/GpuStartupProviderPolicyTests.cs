using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuStartupProviderPolicyTests
{
    [Fact]
    public async Task CorruptExistingGpuPolicyCannotBeReplacedByAnEmptyDocument()
    {
        using var root = new OwnedRoot();
        var policy = Software();
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        await store.SaveSoftwarePolicyAsync(policy, CancellationToken.None);
        var path = System.IO.Path.Combine(root.Path, "UserData", "SoftwareProfiles", "gpu-placement-policies.local.json");
        File.WriteAllText(path, "{invalid");
        var reopened = new JsonGpuPlacementPolicyStore(root.Environment);

        await Assert.ThrowsAsync<JsonException>(() => reopened.GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<JsonException>(() => reopened.SaveSoftwarePolicyAsync(policy, CancellationToken.None));
        Assert.Equal("{invalid", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitEmptyOrRejectedProvidersNeverRestoreDefaults(bool unknown)
    {
        var software = Software() with { AllowedProviders = unknown ? ["not-a-provider"] : [] };
        var process = Process(software, "target.exe", false) with { AllowedProviders = software.AllowedProviders };
        foreach (var policy in new[] { ResolvedGpuPlacementPolicy.FromSoftware(software), ResolvedGpuPlacementPolicy.FromProcess(process, software) })
        {
            Assert.Empty(policy.AllowedProviders);
            Assert.Empty(policy.GetStartupProviders(GpuGraphicsApi.D3D11));
            Assert.False(policy.AllowsRuntimeShimExecution());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveReloadAndResolverPreserveProviderRevocation(bool processOverride, bool unknown)
    {
        using var root = new OwnedRoot();
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        var software = Software();
        var process = Process(software, root.Target, !processOverride);
        await store.SaveSoftwarePolicyAsync(software, CancellationToken.None);
        await store.SaveProcessPolicyAsync(process, CancellationToken.None);
        string[] revoked = unknown ? ["not-a-provider"] : [];
        if (processOverride)
            Assert.Empty((await store.SaveProcessPolicyAsync(process with { AllowedProviders = revoked }, CancellationToken.None)).AllowedProviders);
        else
            Assert.Empty((await store.SaveSoftwarePolicyAsync(software with { AllowedProviders = revoked }, CancellationToken.None)).AllowedProviders);

        var reloaded = new JsonGpuPlacementPolicyStore(root.Environment);
        var document = await reloaded.GetAsync(CancellationToken.None);
        var savedSoftware = Assert.Single(document.SoftwarePolicies);
        var savedProcess = Assert.Single(document.ProcessPolicies);
        Assert.Empty(processOverride ? savedProcess.AllowedProviders : savedSoftware.AllowedProviders);
        var decision = await Resolver(reloaded, document, root.Environment).ResolveAsync(new(root.Target), CancellationToken.None);
        Assert.Equal("startup-provider-not-allowed", decision.Status);
        Assert.Equal(GpuStartupPlacementDecisionKinds.PassThrough, decision.Decision);
        Assert.Empty(decision.StartupProviders);
        Assert.Null(decision.PolicyPath);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "UserData", "GpuPlacement")));
    }

    [Fact]
    public async Task OmittedProvidersStillUseDeclaredDefaults()
    {
        using var root = new OwnedRoot();
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        var software = Software() with { AllowedProviders = null! };
        var saved = await store.SaveSoftwarePolicyAsync(software, CancellationToken.None);
        var reloaded = Assert.Single((await new JsonGpuPlacementPolicyStore(root.Environment).GetAsync(CancellationToken.None)).SoftwarePolicies);
        foreach (var policy in new[] { software, saved, reloaded })
        {
            var resolved = ResolvedGpuPlacementPolicy.FromSoftware(policy);
            Assert.Equal(GpuPlacementProviderIds.Defaults.Order(StringComparer.OrdinalIgnoreCase), resolved.AllowedProviders.Order(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(new[] { GpuPlacementProviderIds.D3dDeviceCreateShim }, resolved.GetStartupProviders(GpuGraphicsApi.D3D11));
            Assert.DoesNotContain(GpuPlacementProviderIds.VulkanExplicitLayer, resolved.AllowedProviders);
        }
    }

    [Theory]
    [InlineData(GpuPlacementProviderIds.VulkanExplicitLayer, true)]
    [InlineData(GpuPlacementProviderIds.VulkanImplicitLayer, false)]
    public async Task SavedVulkanProviderKeepsItsMeaning(string provider, bool enabled)
    {
        using var root = new OwnedRoot();
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        await store.SaveSoftwarePolicyAsync(Software() with { AllowedProviders = [$" {provider.ToUpperInvariant()} ", provider] }, CancellationToken.None);
        var reloaded = Assert.Single((await new JsonGpuPlacementPolicyStore(root.Environment).GetAsync(CancellationToken.None)).SoftwarePolicies);
        Assert.Single(reloaded.AllowedProviders);
        var resolved = ResolvedGpuPlacementPolicy.FromSoftware(reloaded);
        Assert.Equal(enabled ? new[] { GpuPlacementProviderIds.VulkanExplicitLayer } : [], resolved.GetStartupProviders(GpuGraphicsApi.Vulkan));
        Assert.Equal(enabled ? GpuPlacementProviderIds.VulkanExplicitLayer : null,
            resolved.GetRuntimeProvider(GpuGraphicsApi.Vulkan));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualResolverPreparesOneSharedPolicyForSelectedProviders(bool both, bool processOverride)
    {
        using var root = new OwnedRoot();
        string[] providers = both
            ? [GpuPlacementProviderIds.D3dDeviceCreateShim, GpuPlacementProviderIds.VulkanExplicitLayer]
            : [GpuPlacementProviderIds.VulkanExplicitLayer];
        var software = Software() with { AllowedProviders = processOverride ? [GpuPlacementProviderIds.D3dDeviceCreateShim] : providers };
        var process = Process(software, root.Target, !processOverride) with { AllowedProviders = providers };
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        await store.SaveSoftwarePolicyAsync(software, CancellationToken.None);
        await store.SaveProcessPolicyAsync(process, CancellationToken.None);
        var document = await store.GetAsync(CancellationToken.None);
        var decision = await Resolver(store, document, root.Environment).ResolveAsync(new(root.Target), CancellationToken.None);
        Assert.Equal(GpuStartupPlacementDecisionKinds.Inject, decision.Decision);
        Assert.Equal(new[] { GpuPlacementProviderIds.VulkanExplicitLayer }, decision.StartupProviders);
        Assert.NotNull(decision.PolicyPath);
        Assert.StartsWith(Path.Combine(root.Path, "UserData", "GpuPlacement", "D3D11ProxyShim"), decision.PolicyPath, StringComparison.OrdinalIgnoreCase);
        var text = await File.ReadAllTextAsync(decision.PolicyPath);
        Assert.Contains("provider=d3d-device-create-shim", text, StringComparison.Ordinal);
        Assert.Contains("mode=highPerformance", text, StringComparison.Ordinal);
        Assert.Contains($"startupProviders={GpuPlacementProviderIds.VulkanExplicitLayer}\n", GpuLaunchBrokerProtocol.Serialize(decision), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GpuPlacementPolicyModes.Disabled, GpuPlacementSchedulingModes.Precise)]
    [InlineData(GpuPlacementPolicyModes.Preview, GpuPlacementSchedulingModes.Precise)]
    [InlineData(GpuPlacementPolicyModes.Manual, GpuPlacementSchedulingModes.Ordinary)]
    [InlineData(GpuPlacementPolicyModes.Auto, GpuPlacementSchedulingModes.Ordinary)]
    public async Task DisallowedShimNeverPreparesAnyApi(string mode, string schedulingMode)
    {
        using var root = new OwnedRoot();
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        var software = Software() with { EnabledMode = mode, SchedulingMode = schedulingMode };
        await store.SaveSoftwarePolicyAsync(software, CancellationToken.None);
        await store.SaveProcessPolicyAsync(Process(software, root.Target, true), CancellationToken.None);
        var reloaded = new JsonGpuPlacementPolicyStore(root.Environment);
        var document = await reloaded.GetAsync(CancellationToken.None);
        var resolved = ResolvedGpuPlacementPolicy.FromSoftware(Assert.Single(document.SoftwarePolicies));
        Assert.Empty(resolved.GetStartupProviders(GpuGraphicsApi.Vulkan));
        Assert.False(resolved.AllowsRuntimeShimExecution());
        var decision = await Resolver(reloaded, document, root.Environment).ResolveAsync(new(root.Target), CancellationToken.None);
        Assert.Equal("startup-provider-not-allowed", decision.Status);
        Assert.Null(decision.PolicyPath);
        Assert.Empty(decision.StartupProviders);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "UserData", "GpuPlacement")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalDisabledNeverPreparesAnyApi(bool processOverride)
    {
        using var root = new OwnedRoot();
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        var software = Software();
        await store.SaveSoftwarePolicyAsync(software, CancellationToken.None);
        await store.SaveProcessPolicyAsync(Process(software, root.Target, !processOverride), CancellationToken.None);
        var decision = await Resolver(store, await store.GetAsync(CancellationToken.None), root.Environment, globalEnabled: false)
            .ResolveAsync(new(root.Target), CancellationToken.None);
        Assert.Equal("global-provider-disabled", decision.Status);
        Assert.Null(decision.PolicyPath);
        Assert.Empty(decision.StartupProviders);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "UserData", "GpuPlacement")));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "ResourceManager.VulkanPlacementLayer.dll")]
    [InlineData(false, "ResourceManager.VulkanPlacementLayer.json")]
    [InlineData(true, "ResourceManager.VulkanPlacementLayer.dll")]
    [InlineData(true, "ResourceManager.GpuPlacementShim.dll")]
    [InlineData(true, "ResourceManager.GpuPlacementBootstrap.dll")]
    public void RequiredArtifactsMatchTheExactProviderSet(bool both, string omitted)
    {
        using var root = new OwnedRoot();
        File.WriteAllBytes(Path.Combine(root.Path, WindowsIfeoGpuLaunchInterceptionRegistry.BrokerFileName), []);
        var shim = Directory.CreateDirectory(Path.Combine(root.Path, "GpuPlacementShim")).FullName;
        var names = new List<string> { GpuStartupProviderArtifacts.VulkanLayerFileName, GpuStartupProviderArtifacts.VulkanManifestFileName };
        if (both) names.AddRange([WindowsGpuPlacementInjector.RuntimeProviderFileName, WindowsGpuPlacementInjector.StartupBootstrapFileName]);
        foreach (var name in names.Where(name => name != omitted)) File.WriteAllBytes(Path.Combine(shim, name), []);
        string[] providers = both ? [GpuPlacementProviderIds.D3dDeviceCreateShim, GpuPlacementProviderIds.VulkanExplicitLayer] : [GpuPlacementProviderIds.VulkanExplicitLayer];
        Assert.Equal(omitted.Length == 0 ? [] : new[] { Path.Combine("GpuPlacementShim", omitted) }, GpuStartupProviderArtifacts.MissingFiles(root.Path, providers));
        if (!both) Assert.False(File.Exists(Path.Combine(shim, WindowsGpuPlacementInjector.RuntimeProviderFileName)));
    }

    [Fact]
    public async Task PolicyWriteFailureDoesNotAuthorizeStartupProviders()
    {
        using var root = new OwnedRoot();
        var store = new JsonGpuPlacementPolicyStore(root.Environment);
        var software = Software();
        await store.SaveSoftwarePolicyAsync(software, CancellationToken.None);
        await store.SaveProcessPolicyAsync(Process(software, root.Target, true), CancellationToken.None);
        File.WriteAllText(Path.Combine(root.Path, "UserData", "GpuPlacement"), "owned failure fixture");
        var decision = await Resolver(store, await store.GetAsync(CancellationToken.None), root.Environment).ResolveAsync(new(root.Target), CancellationToken.None);
        Assert.Equal("policy-write-failed", decision.Status);
        Assert.Equal(GpuStartupPlacementDecisionKinds.PassThrough, decision.Decision);
        Assert.Empty(decision.StartupProviders);
        Assert.Null(decision.PolicyPath);
    }

    [Fact]
    public async Task ConfigurationReportIsPersistedWithoutClaimingDeviceSelection()
    {
        using var root = new OwnedRoot();
        const string message = "Vulkan child environment configured, device selection not yet observed";
        var store = new JsonGpuLaunchExecutionReportStore(root.Environment);
        await store.RecordAsync(new(root.Target, "software", "process", 42, GpuLaunchExecutionOutcomes.StartupConfigured,
            message, GpuPlacementTargets.HighPerformanceGpu, null, null, DateTimeOffset.UnixEpoch), CancellationToken.None);
        var report = Assert.Single(await new JsonGpuLaunchExecutionReportStore(root.Environment).GetLatestAsync(CancellationToken.None));
        Assert.Equal(GpuLaunchExecutionOutcomes.StartupConfigured, report.Outcome);
        Assert.Equal(message, report.Message);
    }

    private static GpuPlacementSoftwarePolicy Software() => GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
    {
        EnabledMode = GpuPlacementPolicyModes.Manual,
        SchedulingMode = GpuPlacementSchedulingModes.Precise,
        StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu,
        AllowedProviders = [GpuPlacementProviderIds.VulkanExplicitLayer]
    };

    private static GpuPlacementProcessPolicy Process(GpuPlacementSoftwarePolicy software, string path, bool inherit) => new(
        software.SoftwareId, "process", "target.exe", path, inherit, GpuPlacementPolicyModes.Manual, GpuPlacementRiskLevels.Low,
        software.AllowedProviders, GpuPlacementTargets.SystemDefaultGpu, GpuPlacementExplicitSelectionModes.DefaultSkip,
        null, DateTimeOffset.UnixEpoch, true);

    private static GpuStartupPlacementResolver Resolver(JsonGpuPlacementPolicyStore store, GpuPlacementPolicyDocument document, IHostEnvironment environment, bool globalEnabled = true)
    {
        var software = Assert.Single(document.SoftwarePolicies);
        var process = Assert.Single(document.ProcessPolicies);
        var processes = new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase);
        if (!process.Inherit) processes[CompiledBaseScorePlan.CreateProcessPolicyKey(software.SoftwareId, process.ProcessKey)] = ResolvedGpuPlacementPolicy.FromProcess(process, software);
        var plan = new CompiledGpuPlacementPlan(globalEnabled, new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            [software.SoftwareId] = ResolvedGpuPlacementPolicy.FromSoftware(software)
        }, processes);
        return new(store, new PlanProvider(CompiledRuntimePlan.Default with { GpuPlacement = plan }),
            new D3d11ProxyShimRuntime(environment), new KnownHistory(process));
    }

    private sealed class KnownHistory(GpuPlacementProcessPolicy process) : IGpuPlacementProcessHistoryStore
    {
        public Task<GpuPlacementSoftwareProcessHistory> GetSoftwareHistoryAsync(string id, string name, CancellationToken cancellationToken)
            => Task.FromResult(new GpuPlacementSoftwareProcessHistory(id, name,
                [new(process.ProcessKey, process.ProcessName, process.ExecutablePath, "x64", DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, 1, null, ["fixture"], 85, GpuGraphicsApi.Vulkan)], DateTimeOffset.UnixEpoch));
        public Task<GpuPlacementSoftwareProcessHistory> ObserveAsync(GpuPlacementProcessObservationRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Startup must only read previously recorded API information.");
        public Task<GpuPlacementSoftwareProcessHistory> SaveFirstGraphicsApiAsync(string softwareId, string softwareName,
            GpuPlacementProcessInstance instance, GpuGraphicsApi graphicsApi, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Startup must only read previously recorded API information.");
    }

    private sealed class PlanProvider(CompiledRuntimePlan current) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = current;
        public RuntimePlanPublicationLease AcquirePublicationLease() => RuntimePlanPublicationLease.CreateUntracked(Current, 1);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
        {
            Current = plan;
            return new(plan, 1, []);
        }
    }

    private sealed class OwnedRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gpu-startup-policy-{Guid.NewGuid():N}");
        public string Target => System.IO.Path.Combine(Path, "target.exe");
        public IHostEnvironment Environment { get; }
        public OwnedRoot()
        {
            Directory.CreateDirectory(Path);
            File.WriteAllBytes(Target, []);
            Environment = new TestEnvironment(Path);
        }

        public void Dispose()
        {
            var directories = new List<string>();
            var files = new List<string>();
            var pending = new Queue<string>();
            pending.Enqueue(Path);
            while (pending.TryDequeue(out var directory))
            {
                Assert.True(directories.Count + pending.Count < 64 && files.Count < 128, "Owned fixture cleanup bound exceeded.");
                Assert.False(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint));
                directories.Add(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
                    if (attributes.HasFlag(FileAttributes.Directory)) pending.Enqueue(entry);
                    else files.Add(entry);
                }
            }
            foreach (var file in files) File.Delete(file);
            foreach (var directory in directories.OrderByDescending(static path => path.Length)) Directory.Delete(directory);
        }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
