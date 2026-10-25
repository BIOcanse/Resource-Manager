using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPresentGraphicsApiTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(55)]
    [InlineData(56)]
    public void DxgiPresentIdentifiesOnlyTheCombinedRoute(int eventId)
        => Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12,
            PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.DxgiProvider, eventId));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void D3d9PresentIdentifiesD3d9(int eventId)
        => Assert.Equal(GpuGraphicsApi.D3D9,
            PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.Direct3D9Provider, eventId));

    [Fact]
    public async Task NoPresentOrOtherProvidersDoNotIdentifyAnApi()
    {
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.GraphicsKernelProvider, 184));
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.DxgiProvider, 1));
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(Guid.NewGuid(), 42));
        await using var harness = PresentFrameEtwTestHarness.Create();
        var source = harness.Source;
        await source.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze); // No real ETW session in recorded tests.
        var lease = source.AcquireGraphicsApiSubscription(Instance());
        await PresentFrameEtwTestHarness.ReadyAsync(lease);
        Assert.Null(lease.Read());
        Assert.Empty(harness.Sessions.Snapshot());
        await PresentFrameEtwTestHarness.ReleaseAsync(lease);
    }

    [Fact]
    public async Task EventsUseTheSuppliedBirthAndStopOrPidReuseInvalidatesThem()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var source = harness.Source;
        await source.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze);
        var instance = Instance();
        var lease = source.AcquireGraphicsApiSubscription(instance);
        await PresentFrameEtwTestHarness.ReadyAsync(lease);
        var birth = DateTime.FromFileTimeUtc((long)instance.ProcessStartKey);
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, instance.ProcessId, birth.AddTicks(-1));
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, instance.ProcessId + 1, birth);
        Assert.Null(lease.Read());
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 43, instance.ProcessId, birth);
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, lease.Read());
        source.ObserveGraphicsApiProcessLifetime(instance.ProcessId, (long)instance.ProcessStartKey + 1);
        Assert.Null(lease.Read());
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.Direct3D9Provider, 1, instance.ProcessId, birth.AddSeconds(1));
        Assert.Null(lease.Read());
        var second = source.AcquireGraphicsApiSubscription(instance);
        await PresentFrameEtwTestHarness.ReadyAsync(second);
        source.ObserveGraphicsApiProcessLifetime(instance.ProcessId, null);
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, instance.ProcessId, birth);
        Assert.Null(second.Read());
        await PresentFrameEtwTestHarness.ReleaseAsync(lease);
        await PresentFrameEtwTestHarness.ReleaseAsync(second);
    }

    [Fact]
    public async Task GraphicsApiHistoryKeepsItsObservationWindowPollingAndVisibleReason()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), GpuPlacementProcessHistoryObserver.ObservationWindow);
        Assert.Equal(TimeSpan.FromSeconds(5), GpuPlacementProcessHistoryObserver.RefreshInterval);
        await using var harness = PresentFrameEtwTestHarness.Create();
        var lease = harness.Source.AcquireGraphicsApiSubscription(Instance());
        await PresentFrameEtwTestHarness.ReadyAsync(lease);
        var dependency = Assert.Single(Assert.Single(harness.Controller.Read().Blocks).ActiveDependencies);
        Assert.Equal("graphics-api-history", dependency.Owner);
        Assert.Equal("present-graphics-api-subscription", dependency.Reason.ConfigurationOrDependency);
        Assert.Equal(new[] { "graphics-api" }, dependency.Outputs);
        await PresentFrameEtwTestHarness.ReleaseAsync(lease);
    }

    [Fact]
    public async Task IdentificationCommitsEtwEvidenceAndReleasesAllInstancesOfThatExecutable()
    {
        using var files = new Files();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var source = new Presents();
        using var owner = Owner(files, history, source);
        var now = TimeProvider.System.GetTimestamp();
        var snapshot = Snapshot(Process(files), Process(files) with { ProcessId = 43 });
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        Assert.Equal(2, source.Leases.Count);
        source.Leases[0].Api = GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12;
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        Assert.All(source.Leases, lease => Assert.True(lease.Disposed));
        var saved = Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Equal(source.Leases[0].Api, saved.GraphicsApi);
        Assert.Equal(new[] { "etw-present" }, saved.EvidenceSources);
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        Assert.Equal(2, source.Leases.Count);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    [InlineData("excluded")]
    [InlineData("unregistered")]
    public async Task EndingTheAttemptReleasesWithoutWritingOrRepeating(string end)
    {
        using var files = new Files();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var source = new Presents();
        var plans = Plans();
        using var owner = Owner(files, history, source, plans);
        var now = TimeProvider.System.GetTimestamp();
        var snapshot = Snapshot(Process(files));
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        var lease = Assert.Single(source.Leases);
        if (end == "excluded") plans.Publish(plans.Current with
        {
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                { ["software"] = ResolvedGpuPlacementPolicy.FromSoftware(Policy() with { GpuSchedulingExcluded = true }) }, new Dictionary<string, ResolvedGpuPlacementPolicy>())
        });
        await owner.ReconcileAsync(end == "exit" ? Snapshot() : end == "unavailable" ? null : snapshot,
            end == "unregistered" ? new HashSet<string>() : Registered,
            now + (end == "timeout" ? 31 * TimeProvider.System.TimestampFrequency : 0), default);
        Assert.True(lease.Disposed);
        Assert.False(File.Exists(files.HistoryPath));
        if (end != "exit")
        {
            plans.Publish(Plans().Current);
            await owner.ReconcileAsync(snapshot, Registered, now + 40 * TimeProvider.System.TimestampFrequency, default);
            Assert.Single(source.Leases);
        }
    }

    [Fact]
    public async Task ExcludedUnregisteredAndPreviouslyIdentifiedSoftwareDoNotSubscribe()
    {
        using var files = new Files();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var source = new Presents();
        using var owner = Owner(files, history, source, new(Policy() with { GpuSchedulingExcluded = true }));
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        Assert.Empty(source.Leases);
        using var other = Owner(files, history, source);
        await other.ReconcileAsync(Snapshot(Process(files)), new HashSet<string>(), 0, default);
        await history.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.D3D9, default);
        await other.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        Assert.Empty(source.Leases);
    }

    [Fact]
    public async Task TimeoutDoesNotRepeatUntilExitAndPidReuseGetsANewAttempt()
    {
        using var files = new Files();
        var source = new Presents();
        using var owner = Owner(files, new JsonGpuPlacementProcessHistoryStore(files), source);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 31 * TimeProvider.System.TimestampFrequency, default);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 40 * TimeProvider.System.TimestampFrequency, default);
        Assert.Single(source.Leases);
        await owner.ReconcileAsync(Snapshot(Process(files) with { ProcessStartKey = 2 }), Registered,
            41 * TimeProvider.System.TimestampFrequency, default);
        Assert.Equal(2, source.Leases.Count);
        await owner.ReconcileAsync(Snapshot(), Registered, 42 * TimeProvider.System.TimestampFrequency, default);
        Assert.All(source.Leases, lease => Assert.True(lease.Disposed));
    }

    [Fact]
    public async Task SavedPresentEvidenceChangesStartupFromUnknownUsingTheExistingResolver()
    {
        using var files = new Files();
        var policies = new JsonGpuPlacementPolicyStore(files, NullLogger<JsonGpuPlacementPolicyStore>.Instance);
        var policy = Policy() with { StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu };
        await policies.SaveSoftwarePolicyAsync(policy, default);
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var observed = Assert.Single((await history.ObserveAsync(files.Request(), default)).Processes);
        await policies.SaveProcessPolicyAsync(GpuPlacementPolicyDefaults.CreateProcessPolicy("software", observed) with
            { StartupInterceptionEnabled = true }, default);
        var resolver = new GpuStartupPlacementResolver(policies, new PlanProvider(policy),
            new D3d11ProxyShimRuntime(files), history, new EmptyStartupModuleSettingsStore(), ResourceManager.Shared.Packages.PackageRuntimeCatalog.Current,
            new SoftwareRegistry(), NullLogger<GpuStartupPlacementResolver>.Instance);
        Assert.Equal("graphics-api-unidentified", (await resolver.ResolveAsync(new(files.Target), default)).Status);
        var source = new Presents();
        using var owner = Owner(files, history, source);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        Assert.Single(source.Leases).Api = GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12;
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 1, default);
        var decision = await resolver.ResolveAsync(new(files.Target), default);
        Assert.Equal(GpuStartupPlacementDecisionKinds.Inject, decision.Decision);
        Assert.Equal(GpuPlacementProviderIds.D3dDeviceCreateShim, Assert.Single(decision.StartupProviders));
    }

    [Fact]
    public async Task HostShutdownReleasesBothThePresentAndProcessObservationLeases()
    {
        using var files = new Files();
        var source = new Presents();
        var processSource = new Processes(Snapshot(Process(files)));
        var record = new SoftwareRecord("software", "Software", "game", "installed", [], [],
            new(false, "None", "missing_uninstall_entry"), null);
        using var owner = new GpuPlacementProcessHistoryObserver(processSource, new SoftwareRegistry([record]),
            Plans(), new JsonGpuPlacementProcessHistoryStore(files), source,
            NullLogger<GpuPlacementProcessHistoryObserver>.Instance);
        await owner.StartAsync(default);
        var lease = await source.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.StopAsync(default);
        Assert.True(lease.Disposed);
        Assert.True(processSource.Lease.Disposed);
    }

    private static readonly IReadOnlySet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "software" };
    private static GpuPlacementSoftwarePolicy Policy() => GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software");
    private static PlanProvider Plans() => new(Policy());
    private static GpuPlacementProcessInstance Instance() => new(42, (ulong)DateTime.UtcNow.ToFileTimeUtc(), "target", @"C:\target.exe");
    private static SchedulingProcessFact Process(Files files)
        => new(42, 1, "target", files.Target, "software", "Software", "game", "game", 1,
            SchedulingProcessMetricMask.RuntimeState, 0, 0, 1, []);
    private static SchedulingProcessFactSnapshot Snapshot(params SchedulingProcessFact[] processes)
        => new(SamplingObservationStatus.Current, 1, DateTime.UtcNow.Ticks, (uint)processes.Length,
            (uint)processes.Length, 0, 0, SchedulingProcessMetricMask.RuntimeState,
            SchedulingProcessMetricMask.RuntimeState, processes);
    private static GpuPlacementProcessHistoryObserver Owner(Files files,
        IGpuPlacementProcessHistoryStore history, Presents source, PlanProvider? plans = null)
        => new(new UnusedProcesses(), new SoftwareRegistry(), plans ?? Plans(), history, source,
            NullLogger<GpuPlacementProcessHistoryObserver>.Instance);

    private sealed class Presents : IPresentGraphicsApiObservationSource
    {
        internal List<Subscription> Leases { get; } = [];
        internal TaskCompletionSource<Subscription> Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IPresentGraphicsApiSubscription AcquireGraphicsApiSubscription(GpuPlacementProcessInstance process)
        {
            var lease = new Subscription();
            Leases.Add(lease);
            Acquired.TrySetResult(lease);
            return lease;
        }
    }

    private sealed class Subscription : IPresentGraphicsApiSubscription
    {
        internal GpuGraphicsApi? Api { get; set; }
        internal bool Disposed { get; private set; }
        public GpuGraphicsApi? Read() => Disposed ? null : Api;
        public void Dispose() => Disposed = true;
    }

    private sealed class UnusedProcesses : ISchedulingProcessFactObservationSource
    {
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
            => throw new NotSupportedException();
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
            => throw new NotSupportedException();
    }

    private sealed class Processes(SchedulingProcessFactSnapshot snapshot) : ISchedulingProcessFactObservationSource
    {
        internal Subscription Lease { get; } = new();
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
        {
            Assert.Equal(SchedulingProcessMetricMask.RuntimeState, mask);
            Assert.Equal(GpuPlacementProcessHistoryObserver.RefreshInterval, interval);
            return Lease;
        }
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) => snapshot;
    }

    private sealed class PlanProvider(GpuPlacementSoftwarePolicy policy) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = CompiledRuntimePlan.Default with
        {
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                { [policy.SoftwareId] = ResolvedGpuPlacementPolicy.FromSoftware(policy) },
                new Dictionary<string, ResolvedGpuPlacementPolicy>())
        };
        public RuntimePlanPublicationLease AcquirePublicationLease() => RuntimePlanPublicationLease.CreateUntracked(Current, 1);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan) { Current = plan; return new(plan, 1, []); }
    }

    private sealed class SoftwareRegistry(IReadOnlyList<SoftwareRecord>? records = null) : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<SoftwareRecord>>(records ?? []);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken token) => GetSoftwareAsync(token);
    }

    private sealed class Files : IHostEnvironment, IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH") ?? Path.GetTempPath(),
            $"present-api-{Guid.NewGuid():N}");
        public string Target => Path.Combine(Root, "target.exe");
        public string HistoryPath => Path.Combine(Root, "UserData", "SoftwareProfiles", "gpu-placement-process-history.local.json");
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PresentApiTests";
        public string ContentRootPath { get => Root; set => throw new NotSupportedException(); }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public Files() { Directory.CreateDirectory(Root); File.WriteAllBytes(Target, []); }
        public GpuPlacementProcessInstance Instance() => new(42, 1, "target", Target);
        public GpuPlacementProcessObservationRequest Request() => new("software", "Software", [new("target", Target, "x64", 42, ["fixture"])]);
        public void Dispose()
        {
            // Only files created by this fixture; leave no recursive cleanup or arbitrary path traversal.
            foreach (var path in new[] { HistoryPath, Target }) if (File.Exists(path)) File.Delete(path);
            // The real startup resolver owns its generated policy files; the registered scratch
            // workspace retains them until official task completion.
        }
    }
}
