using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace Resource_Manager_APP.Tests;

public sealed class AmdSmuHardwareBindingTests
{
    [Fact]
    public void MissingModuleStaysUnavailableWithoutOpeningOrResolvingAgainUntilComponentChanges()
    {
        var revisions = new ManagementInventoryRevisionOwner();
        var resolves = 0;
        var opens = 0;
        string? path = null;
        using var binding = new AmdSmuCcdBinding(() => { resolves++; return path; },
            () => revisions.Current.ComponentRevision,
            (target, module) => new(target.Family, target.Model, 1,
                () => { opens++; return new RecordedSession(); }));
        for (var capture = 0; capture < 50; capture++)
        {
            var result = binding.Discover(Topology(), 16);
            Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, result.Status);
            Assert.Empty(result.Identities);
            Assert.Null(binding.Read(Identity()));
        }
        Assert.Equal(1, resolves);
        Assert.Equal(0, opens);

        path = "installed-AMDFamily17.bin";
        Assert.Empty(binding.Discover(Topology(), 16).Identities);
        Assert.Equal(1, resolves);
        revisions.ObserveComponentContentChanged();
        var installed = binding.Discover(Topology(), 16);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, installed.Status);
        Assert.Equal(51, binding.Read(Assert.Single(installed.Identities)));
        Assert.Equal(2, resolves);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void RemovalAndRepairReleaseOldSessionAndDoNotReadPriorCatalogBeforeRediscovery()
    {
        var revisions = new ManagementInventoryRevisionOwner();
        string? path = "original-AMDFamily17.bin";
        var sessions = new List<RecordedSession>();
        var selectedPaths = new List<string>();
        using var binding = new AmdSmuCcdBinding(() => path, () => revisions.Current.ComponentRevision,
            (target, module) => new(target.Family, target.Model, 1, () =>
            {
                selectedPaths.Add(module);
                var session = new RecordedSession();
                sessions.Add(session);
                return session;
            }));
        var identity = Assert.Single(binding.Discover(Topology(), 16).Identities);
        Assert.Equal(51, binding.Read(identity));

        path = null;
        revisions.ObserveComponentContentChanged();
        Assert.Null(binding.Read(identity));
        Assert.Equal(1, sessions[0].Disposals);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, binding.Discover(Topology(), 16).Status);

        path = "repaired-AMDFamily17.bin";
        revisions.ObserveComponentContentChanged();
        Assert.Null(binding.Read(identity));
        Assert.Single(sessions);
        Assert.Single(binding.Discover(Topology(), 16).Identities);
        Assert.Equal(51, binding.Read(identity));
        Assert.Equal(new[] { "original-AMDFamily17.bin", "repaired-AMDFamily17.bin" }, selectedPaths);
        binding.Dispose();
        Assert.All(sessions, session => Assert.Equal(1, session.Disposals));
        Assert.Throws<ObjectDisposedException>(() => binding.Discover(Topology(), 16));
        Assert.Throws<ObjectDisposedException>(() => binding.Read(identity));
    }

    [Fact]
    public void SamePathRepairReopensTheSessionOnceAfterComponentSignal()
    {
        var revisions = new ManagementInventoryRevisionOwner();
        var opens = 0;
        var first = new RecordedSession();
        using var binding = new AmdSmuCcdBinding(() => "AMDFamily17.bin", () => revisions.Current.ComponentRevision,
            (target, module) => new(target.Family, target.Model, 1,
                () => ++opens == 1 ? first : new RecordedSession()));
        Assert.Single(binding.Discover(Topology(), 16).Identities);
        revisions.ObserveComponentContentChanged();
        Assert.Single(binding.Discover(Topology(), 16).Identities);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(2, opens);
        Assert.Single(binding.Discover(Topology(), 16).Identities);
        Assert.Equal(2, opens);
    }

    [Fact]
    public void MissingModulePreservesNotApplicableAndCapacitySemantics()
    {
        using var binding = new AmdSmuCcdBinding(() => null, static () => "0",
            (_, _) => throw new InvalidOperationException("Missing modules must not open a reader."));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, binding.Discover(Topology("GenuineIntel"), 16).Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, binding.Discover(Topology(), 0).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => binding.Discover(Topology(), 17));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, binding.Discover(null, 16).Status);
    }

    [Fact]
    public void ProductionBindingWithAbsentModuleReturnsUnavailableInsteadOfModuleOpenException()
    {
        using var binding = AmdSmuHardwareBinding.CreateCcdResource(
            Path.Combine(Path.GetTempPath(), "missing-amd-smu-" + Guid.NewGuid().ToString("N")), static () => "0");
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, binding.Discover(Topology(), 16).Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, binding.Discover(Topology(), 16).Status);
    }

    [Fact]
    public async Task ExplicitComponentChangePublishesEvenWhenStatusFingerprintDoesNotChange()
    {
        var revisions = new ManagementInventoryRevisionOwner();
        await using var subscription = revisions.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        var next = subscription.MoveNextAsync().AsTask();
        revisions.ObserveComponentContentChanged();
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("1", subscription.Current.ComponentRevision);
        Assert.Equal("0", subscription.Current.SoftwareRevision);
        Assert.Equal("0", subscription.Current.BrowserRuntimeRevision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerificationInvalidatesModuleSelectionOnlyAfterSuccessfulPayloadAcquisition(bool satisfied)
    {
        var revisions = new ManagementInventoryRevisionOwner();
        var manager = new ComponentManager(new EmptyDependencies(), new RecordedSampler(), new MissingProvider(),
            new RecordedPayload(satisfied), revisions);
        var before = await manager.GetStatusAsync("amd-smu-pawnio-provider", default);
        Assert.Equal("0", revisions.Current.ComponentRevision);
        var result = await manager.VerifyAsync("amd-smu-pawnio-provider", default);
        Assert.Equivalent(before, result.Status);
        Assert.Equal(satisfied ? "1" : "0", revisions.Current.ComponentRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallInvalidatesModuleSelectionButAlreadyInstalledReuseDoesNot(bool alreadyInstalled)
    {
        var revisions = new ManagementInventoryRevisionOwner();
        var dependencies = new EmptyDependencies(alreadyInstalled);
        var manager = new ComponentManager(dependencies, new RecordedSampler(), new MissingProvider(),
            new RecordedPayload(true), revisions);
        _ = await manager.GetStatusAsync("amd-smu-pawnio-provider", default);
        _ = await manager.InstallAsync("amd-smu-pawnio-provider", true, null, default);
        Assert.Equal(alreadyInstalled ? "0" : "1", revisions.Current.ComponentRevision);
        Assert.Equal(alreadyInstalled ? 0 : 1, dependencies.Launches);
    }

    private static CpuOsTopologyInformation Topology(string vendor = "AuthenticAMD")
    {
        var data = new CpuThreadCpuidData(new(vendor, 0x19, 0x61, 1, "recorded AMD", [], []),
            0xA60F12, null, null, null, null, null, []);
        return new([], []) { Packages = [new("package-opaque", [0, 1])], ThreadCpuid = [new(0, data), new(1, data)] };
    }

    private static AmdCcdCatalogIdentity Identity()
        => new(new("package-opaque", 0x19, 0x61, 0xA60F12, [new(0, 0), new(0, 1)]), 0, 0x59B08);

    private sealed class RecordedSession : IAmdSmnSession
    {
        internal int Disposals;
        public uint ReadRegister(uint address) => address == 0x59B08 ? 0xB20U : 0;
        public void Dispose() => Disposals++;
    }

    private sealed class RecordedPayload(bool satisfied) : IDependencyPayloadAcquisition
    {
        public Task<DependencyPayloadResult> EnsureAsync(OptionalDependencyDefinition definition, CancellationToken cancellationToken)
            => Task.FromResult(new DependencyPayloadResult(satisfied, "recorded payload result", Changed: satisfied));
        public Task<DependencyPayloadResult> InstallAsync(OptionalDependencyDefinition definition, CancellationToken cancellationToken)
            => EnsureAsync(definition, cancellationToken);
    }

    private sealed class MissingProvider : IProviderRuntimeProbe
    {
        public ComponentProviderRuntimeProbeResult Probe(string componentId)
            => new(false, false, ComponentProviderStates.Missing, "recorded missing runtime", null);
    }

    private sealed class RecordedSampler : IMetricSampler
    {
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new HardwareMetricSnapshot(DateTimeOffset.UnixEpoch,
                new CpuMetrics("CPU", 0, false, CpuMetricObservationStatus.NotRequested, 0, 0, 0, 0, 0, "",
                    new CpuSensorMetrics(new("", "", null), null, null, null, null)),
                new(0, 0, 0, false), new(0, 0, 0, false), [], null!, new Dictionary<string, MetricReadingValue>()));
    }

    private sealed class EmptyDependencies(bool alreadyInstalled = false) : IOptionalDependencyManager
    {
        internal int Launches;
        public Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OptionalDependencyStatus>>([]);
        public Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult<OptionalDependencyStatus?>(new(OptionalDependencyCatalog.Find(id)!, "installed",
                "root", "installer", "runtime", "recorded.exe", true, alreadyInstalled, true, true,
                "runtime", null, "recorded", BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.InstalledInManagedRoot)));
        public Task<DependencyVersionOptions> GetVersionOptionsAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OptionalDependencyDownloadResult> DownloadAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken cancellationToken, IProgress<DependencyDownloadProgress>? progress = null)
            => Task.FromResult(new OptionalDependencyDownloadResult(id, "downloaded", "recorded.exe", 0,
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.InstallerDownloaded)));
        public Task<OptionalDependencyLaunchResult> LaunchInstallerAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
            CancellationToken cancellationToken, SoftwareOperationCaller? caller = null)
        {
            Launches++;
            return Task.FromResult(new OptionalDependencyLaunchResult(id, "filesExtracted", "recorded.exe",
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.ComponentFilesExtracted)));
        }
    }
}
