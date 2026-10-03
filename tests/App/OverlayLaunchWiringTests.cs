using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class OverlayLaunchWiringTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    public void RuleSelection_UsesGpuAndOverlayUnion(bool gpu, bool overlay, int expectedCount)
    {
        const string path = @"C:\Apps\Game\game.exe";
        var document = Document(Process(path, gpu));
        var selected = WindowsIfeoGpuLaunchInterceptionRegistry.SelectDesiredPolicies(
            document, overlay ? [path] : []);

        Assert.Equal(expectedCount, selected.Count);
        if (expectedCount != 0)
        {
            Assert.True(Assert.Single(selected).StartupInterceptionEnabled);
            Assert.Equal(path, selected[0].ExecutablePath, ignoreCase: true);
            Assert.Equal("process", selected[0].ProcessKey);
        }
    }

    [Fact]
    public void RemovingOverlayRule_KeepsTheGpuRuleOnlyWhenRequested()
    {
        const string path = @"C:\Apps\Game\game.exe";
        Assert.Empty(WindowsIfeoGpuLaunchInterceptionRegistry.SelectDesiredPolicies(Document(Process(path, false)), []));
        Assert.Single(WindowsIfeoGpuLaunchInterceptionRegistry.SelectDesiredPolicies(Document(Process(path, true)), []));
    }

    [Fact]
    public void OverlayRuleDoesNotRequireAGpuProcessPolicy()
    {
        const string path = @"C:\Apps\Game\game.exe";
        var policy = Assert.Single(WindowsIfeoGpuLaunchInterceptionRegistry.SelectDesiredPolicies(
            Document(), [path]));
        Assert.True(policy.StartupInterceptionEnabled);
        Assert.Equal(path, policy.ExecutablePath, ignoreCase: true);
    }

    [Theory]
    [InlineData(true, "injected", true)]
    [InlineData(true, "external", false)]
    [InlineData(false, "injected", false)]
    public void OverlayRulePaths_ComeFromRegisteredSoftware(bool enabled, string mode, bool expected)
    {
        const string path = @"C:\Apps\Game\game.exe";
        var settings = new PerformanceOverlaySettingsDocument(1,
            [new PerformanceOverlaySettings { SoftwareId = "software", Enabled = enabled, Mode = mode }]);
        var software = new[] { Software(path) };
        var paths = GpuLaunchInterceptionReconciler.SelectOverlayExecutablePaths(settings, software);

        Assert.Equal(expected, paths.Contains(path));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Reconciliation_GatesGpuRulesButPreservesInjectedOverlay(
        bool gpuAvailable, bool overlayEnabled, bool availabilityFails)
    {
        const string gpuPath = @"C:\Apps\Gpu\gpu.exe";
        const string overlayPath = @"C:\Apps\Overlay\overlay.exe";
        var registry = new CapturingRegistry();
        var reconciler = new GpuLaunchInterceptionReconciler(
            new StaticGpuPolicyStore(Document(Process(gpuPath, true))),
            new StaticOverlayStore(new PerformanceOverlaySettingsDocument(1,
                [new PerformanceOverlaySettings { SoftwareId = "software", Enabled = overlayEnabled, Mode = "injected" }])),
            new StaticSoftwareView([Software(overlayPath)]),
            registry,
            new StaticGpuSchedulingAvailability(gpuAvailable, availabilityFails),
            NullLogger<GpuLaunchInterceptionReconciler>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None);

        var desired = WindowsIfeoGpuLaunchInterceptionRegistry.SelectDesiredPolicies(
            registry.GpuDocument!, registry.OverlayPaths);
        Assert.Equal(gpuAvailable && !availabilityFails,
            desired.Any(policy => string.Equals(policy.ExecutablePath, gpuPath, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(overlayEnabled,
            desired.Any(policy => string.Equals(policy.ExecutablePath, overlayPath, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal((gpuAvailable && !availabilityFails ? 1 : 0) + (overlayEnabled ? 1 : 0), desired.Count);
    }

    [Fact]
    public async Task OverlaySettingsFailureDoesNotDiscardGpuLaunchRules()
    {
        var path = Environment.ProcessPath!;
        var registry = new CapturingRegistry();
        var reconciler = new GpuLaunchInterceptionReconciler(
            new StaticGpuPolicyStore(Document(Process(path, true))),
            new FailingOverlaySettingsStore(),
            new StaticSoftwareView([Software(path)]),
            registry,
            new StaticGpuSchedulingAvailability(true),
            NullLogger<GpuLaunchInterceptionReconciler>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Empty(registry.OverlayPaths);
        Assert.True(Assert.Single(registry.GpuDocument!.ProcessPolicies).StartupInterceptionEnabled);
    }

    [Theory]
    [InlineData(false, "injected", false)]
    [InlineData(true, "external", false)]
    [InlineData(true, "injected", true)]
    public async Task Resolver_OverlayOnlyDecisionDoesNotRequireGpuPolicy(
        bool enabled, string mode, bool expectedOverlay)
    {
        var path = Environment.ProcessPath!;
        var resolver = new GpuStartupPlacementResolver(
            new StaticGpuPolicyStore(Document()),
            new StaticPlanProvider(),
            new D3d11ProxyShimRuntime(new TestHostEnvironment(Path.GetTempPath())),
            new JsonGpuPlacementProcessHistoryStore(new TestHostEnvironment(Path.GetTempPath())),
            new StaticOverlayStore(new PerformanceOverlaySettingsDocument(1,
                [new PerformanceOverlaySettings { SoftwareId = "software", Enabled = enabled, Mode = mode }])),
            new StaticSoftwareView([Software(path)]),
            NullLogger<GpuStartupPlacementResolver>.Instance);

        var decision = await resolver.ResolveAsync(new(path), CancellationToken.None);

        Assert.Equal(expectedOverlay, decision.OverlayEnabled);
        Assert.Equal(expectedOverlay ? GpuStartupPlacementDecisionKinds.Inject
            : GpuStartupPlacementDecisionKinds.PassThrough, decision.Decision);
        Assert.Empty(decision.StartupProviders);
        Assert.Null(decision.PolicyPath);
        Assert.Equal(expectedOverlay, GpuLaunchBrokerProtocol.Serialize(decision).Contains(
            "overlayEnabled=true\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void StartupDecisionWire_SeparatesGpuAndOverlay(bool gpu, bool overlay)
    {
        var decision = new GpuStartupPlacementDecision(
            gpu || overlay ? GpuStartupPlacementDecisionKinds.Inject : GpuStartupPlacementDecisionKinds.PassThrough,
            "ready", "configured", @"C:\Apps\game.exe", "software", "process", gpu ? "GPU0" : null,
            null, gpu ? @"C:\Package\UserData\GpuPlacement\gpu-policy.txt" : null,
            null, null, gpu ? [GpuPlacementProviderIds.D3dDeviceCreateShim] : [], overlay);

        var json = JsonSerializer.SerializeToElement(decision, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(overlay, json.GetProperty("overlayEnabled").GetBoolean());
        Assert.Equal(gpu ? 1 : 0, json.GetProperty("startupProviders").GetArrayLength());
        var protocol = GpuLaunchBrokerProtocol.Serialize(decision);
        Assert.Equal(overlay, protocol.Contains("overlayEnabled=true\n", StringComparison.Ordinal));
        Assert.Equal(gpu, protocol.Contains("startupProviders=d3d-device-create-shim\n", StringComparison.Ordinal));
    }

    private static GpuPlacementPolicyDocument Document(params GpuPlacementProcessPolicy[] policies) =>
        new(GpuPlacementPolicyDocumentVersions.Current, [], policies, DateTimeOffset.UnixEpoch);

    private static GpuPlacementProcessPolicy Process(string path, bool enabled) => new(
        "software", "process", Path.GetFileName(path), path, true,
        GpuPlacementPolicyModes.Inherit, GpuPlacementRiskLevels.Low, [],
        GpuPlacementTargets.SystemDefaultGpu, GpuPlacementExplicitSelectionModes.DefaultSkip,
        null, DateTimeOffset.UnixEpoch, enabled);

    private static SoftwareRecord Software(string path) => new(
        "software", "Software", SoftwareKinds.Game, "Game", "registered",
        [], [], string.Empty, new SoftwareOperationCapabilities(false, "", "", ""),
        null, ExecutablePaths: [path]);

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

    private sealed class StaticOverlayStore(PerformanceOverlaySettingsDocument document) : IPerformanceOverlaySettingsStore
    {
        public Task<PerformanceOverlaySettingsDocument> GetAsync(CancellationToken cancellationToken) => Task.FromResult(document);
        public Task<PerformanceOverlaySettings> GetSoftwareAsync(string softwareId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PerformanceOverlaySettings> SaveSoftwareAsync(PerformanceOverlaySettings settings,
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
        public IReadOnlyCollection<string> OverlayPaths { get; private set; } = [];
        public GpuPlacementPolicyDocument? GpuDocument { get; private set; }
        public GpuLaunchInterceptionStatus Apply(GpuPlacementProcessPolicy policy) => throw new NotSupportedException();
        public GpuLaunchInterceptionStatus GetStatus(GpuPlacementProcessPolicy policy) => throw new NotSupportedException();
        public IReadOnlyList<GpuLaunchInterceptionStatus> Reconcile(GpuPlacementPolicyDocument document,
            IReadOnlyCollection<string> overlayExecutablePaths)
        {
            GpuDocument = document;
            OverlayPaths = overlayExecutablePaths;
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

internal sealed class FailingOverlaySettingsStore : IPerformanceOverlaySettingsStore
{
    public Task<PerformanceOverlaySettingsDocument> GetAsync(CancellationToken cancellationToken) =>
        Task.FromException<PerformanceOverlaySettingsDocument>(new IOException("Overlay settings unavailable."));
    public Task<PerformanceOverlaySettings> GetSoftwareAsync(string softwareId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PerformanceOverlaySettings> SaveSoftwareAsync(PerformanceOverlaySettings settings,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
