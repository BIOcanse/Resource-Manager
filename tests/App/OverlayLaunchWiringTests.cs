using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class OverlayLaunchWiringTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    public void RuleSelectionUnionsGpuAndAdmittedModules(bool gpu, bool modules, int count)
    {
        const string path = @"C:\Apps\Game\game.exe";
        var selected = WindowsIfeoGpuLaunchInterceptionRegistry.SelectDesiredPolicies(Document(Process(path, gpu)), modules ? [path] : []);
        Assert.Equal(count, selected.Count);
        if (count > 0) Assert.True(Assert.Single(selected).StartupInterceptionEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModuleOnlyDecisionHasNoGpuPolicyAndGenericWire(bool enabled)
    {
        using var files = new StartupModuleTestCatalog();
        var modules = new MemoryModuleStore(new(1, [new("software", enabled ? ["fixture-module"] : [])]));
        var resolver = new GpuStartupPlacementResolver(new StaticGpuPolicyStore(Document()), new StaticPlanProvider(),
            new D3d11ProxyShimRuntime(files), new JsonGpuPlacementProcessHistoryStore(files), modules, files.Catalog,
            new StaticSoftwareView([Software(files.Image)]), NullLogger<GpuStartupPlacementResolver>.Instance);
        var decision = await resolver.ResolveAsync(new(files.Image), default);
        Assert.Equal(enabled ? GpuStartupPlacementDecisionKinds.Inject : GpuStartupPlacementDecisionKinds.PassThrough, decision.Decision);
        Assert.Equal(enabled ? 1 : 0, decision.StartupModules.Count);
        Assert.Empty(decision.StartupProviders);
        Assert.Null(decision.PolicyPath);
        var wire = GpuLaunchBrokerProtocol.Serialize(decision);
        Assert.Contains("version=2\n", wire);
        Assert.DoesNotContain("overlayEnabled", wire);
        if (enabled) Assert.Contains("module0.id=fixture-module\n", wire);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReconcilerKeepsModulesIndependentOfGpuAvailability(bool available, bool failure)
    {
        using var files = new StartupModuleTestCatalog();
        var registry = new CapturingRegistry();
        var reconciler = new GpuLaunchInterceptionReconciler(new StaticGpuPolicyStore(Document(Process("gpu.exe", true))),
            new MemoryModuleStore(new(1, [new("software", ["fixture-module"])])), files.Catalog,
            new StaticSoftwareView([Software(files.Image)]), registry,
            new StaticGpuSchedulingAvailability(available, failure), NullLogger<GpuLaunchInterceptionReconciler>.Instance);
        await reconciler.ReconcileAsync(default);
        Assert.Equal([files.Image], registry.ModulePaths);
        Assert.Equal(available && !failure ? 1 : 0, registry.GpuDocument!.ProcessPolicies.Count);
    }

    [Fact]
    public async Task UnadmittedSelectionCannotCreateRuleOrDecision()
    {
        using var files = new StartupModuleTestCatalog();
        var settings = new StartupModuleSettingsDocument(1, [new("software", ["unadmitted"])]);
        Assert.Empty(GpuLaunchInterceptionReconciler.SelectModuleExecutablePaths(settings, [Software(files.Image)], files.Catalog));
        var resolver = new GpuStartupPlacementResolver(new StaticGpuPolicyStore(Document()), new StaticPlanProvider(),
            new D3d11ProxyShimRuntime(files), new JsonGpuPlacementProcessHistoryStore(files), new MemoryModuleStore(settings),
            files.Catalog, new StaticSoftwareView([Software(files.Image)]), NullLogger<GpuStartupPlacementResolver>.Instance);
        Assert.False((await resolver.ResolveAsync(new(files.Image), default)).ShouldInject);
    }

    private sealed class MemoryModuleStore(StartupModuleSettingsDocument document) : IStartupModuleSettingsStore
    {
        public Task<StartupModuleSettingsDocument> GetAsync(CancellationToken token) => Task.FromResult(document);
        public Task<StartupModuleSelection> SaveAsync(StartupModuleSelection selection, CancellationToken token) => throw new NotSupportedException();
    }
    private static GpuPlacementPolicyDocument Document(params GpuPlacementProcessPolicy[] policies) => new(GpuPlacementPolicyDocumentVersions.Current, [], policies, DateTimeOffset.UnixEpoch);
    private static GpuPlacementProcessPolicy Process(string path, bool enabled) => new("software", "process", "game.exe", path, true, [], null, DateTimeOffset.UnixEpoch, enabled);
    private static SoftwareRecord Software(string path) => new("software", "Software", SoftwareKinds.Game, "registered", [], [],
        new SoftwareOperationCapabilities(false, "None", "missing_uninstall_entry"), null, ExecutablePaths: [path]);
    private sealed class StaticGpuPolicyStore(GpuPlacementPolicyDocument document) : IGpuPlacementPolicyStore
    {
        public Task<GpuPlacementPolicyDocument> GetAsync(CancellationToken cancellationToken) => Task.FromResult(document);
        public Task<GpuPlacementSoftwarePolicy> GetOrCreateSoftwarePolicyAsync(string softwareId,
            string softwareName, string? softwareKind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GpuPlacementSoftwarePolicy> SaveSoftwarePolicyAsync(GpuPlacementSoftwarePolicy policy,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GpuPlacementProcessPolicy> SaveProcessPolicyAsync(GpuPlacementProcessPolicy policy,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StaticSoftwareView(IReadOnlyList<SoftwareRecord> software) : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken) => Task.FromResult(software);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken) => Task.FromResult(software);
    }

    private sealed class StaticGpuSchedulingAvailability(bool enabled, bool fails = false)
        : IGpuSchedulingAvailability
    {
        public ValueTask<GpuSchedulingAvailability> EvaluateAsync(CancellationToken cancellationToken)
        {
            if (fails)
            {
                throw new IOException("GPU availability unavailable.");
            }

            return ValueTask.FromResult(new GpuSchedulingAvailability(enabled, null));
        }
    }

    private sealed class CapturingRegistry : IGpuLaunchInterceptionRegistry
    {
        public IReadOnlyCollection<string> ModulePaths { get; private set; } = [];
        public GpuPlacementPolicyDocument? GpuDocument { get; private set; }
        public GpuLaunchInterceptionStatus Apply(GpuPlacementProcessPolicy policy) => throw new NotSupportedException();
        public GpuLaunchInterceptionStatus GetStatus(GpuPlacementProcessPolicy policy) => throw new NotSupportedException();
        public IReadOnlyList<GpuLaunchInterceptionStatus> Reconcile(GpuPlacementPolicyDocument document,
            IReadOnlyCollection<string> moduleExecutablePaths)
        {
            GpuDocument = document;
            ModulePaths = moduleExecutablePaths;
            return [];
        }
        public GpuLaunchInterceptionCleanupResult RemoveAllOwnedRules() => throw new NotSupportedException();
    }

    private sealed class StaticPlanProvider : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current => CompiledRuntimePlan.Default;
        public RuntimePlanPublicationLease AcquirePublicationLease() => RuntimePlanPublicationLease.CreateUntracked(Current, 1);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan) => throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "OverlayLaunchWiringTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal sealed class EmptySoftwareRegistryView : ISoftwareRegistryView
{
    public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SoftwareRecord>>([]);

    public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken) =>
        GetSoftwareAsync(cancellationToken);
}
