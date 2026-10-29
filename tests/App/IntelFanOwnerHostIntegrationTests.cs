using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelFanOwnerHostIntegrationTests
{
    [Fact]
    public Task PublicRecordedOwnerAndBothPackageConsumerContract() => IntelFanConsumer.RunRecordedAsync();

    [Fact]
    public void HostHolderConstructionFailureReleasesEarlierFanBorrowAndLeavesCallerResourcesOwned()
    {
        var fixture = new NativeRecording();
        using var directory = Assert.IsType<IntelGclFanDirectoryLease>(fixture.Reader.OpenFanDirectory(RecordedIntelFanResources.Inventory(), 2));
        var configuration = WindowsMetricConfiguration.Default;
        using var workspace = new NativeMetricSnapshotWorkspace(configuration.Configuration, configuration.CatalogConfiguration);
        workspace.ReplaceCatalog(NativeMetricSnapshotCatalogProjection.Create(configuration.CatalogConfiguration, [], [], [], workspace.CreateCatalogHandleMap()), DateTimeOffset.UtcNow);
        var engine = new ThrowingEngineDirectory();
        var fans = directory.Identities.Select((fan, index) => new NativeMetricSnapshotOrdinalCatalogIdentity(index, fan.ExactIdentityKey, 1)
            { IgclFan = fan }).ToArray();
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, fans)
            { NpuEngineDirectory = engine };

        Assert.Throws<ArgumentOutOfRangeException>(() => new HostManagerMetricSnapshotOwner.WorkspaceHolder(0, workspace, topology));
        Assert.Equal(0, engine.Acquires);
        var holder = new HostManagerMetricSnapshotOwner.WorkspaceHolder(1, workspace, topology);
        Assert.Equal(0, engine.Acquires); // Acquisition happens only after the holder has an owner.
        Assert.Throws<InvalidOperationException>(() => holder.RetainDirectories());
        Assert.Equal(1, engine.Acquires);
        Assert.Equal(0, fixture.Sessions[0].Closes);
        Assert.Equal(0, engine.Disposals);
        _ = workspace.ReadCommitted(); // A failed Retain has not silently destroyed the caller's Native workspace.
        holder.Retire();
        holder.CloseIfUnused();
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadCommitted());
        Assert.Equal(0, fixture.Sessions[0].Closes); // The caller's initial directory reference remains live.
        Assert.Equal(0, engine.Disposals);
        directory.Dispose();
        Assert.Equal(1, fixture.Sessions[0].Closes);
    }

    private sealed class ThrowingEngineDirectory : IWindowsNpuEngineDirectory
    {
        internal int Acquires, Disposals;
        public IReadOnlyList<NpuEngineCatalogIdentity> Identities => [];
        public bool IsCurrent(uint maximum, CancellationToken token = default) => true;
        public IDisposable AcquireLease() { Acquires++; throw new InvalidOperationException("Recorded engine lease failure."); }
        public void Dispose() => Disposals++;
    }

    [Fact]
    public async Task UnchangedCatalogProbeReusesOriginalSessionTargetsAndNativeWorkspace()
    {
        var fixture = new NativeRecording();
        using var owner = fixture.Owner();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        var fields = IntelFanConsumer.Fields(await core.DiscoverAsync());
        var first = await core.CollectAsync(fields);
        var before = owner.ReadCachedSnapshot()!;
        var directory = fixture.Record.Fans;
        var again = IntelFanConsumer.Fields(await core.DiscoverAsync());
        Assert.Equal<FieldReference>(fields, again);
        Assert.Equal(before.WorkspaceIdentity, owner.ReadCachedSnapshot()!.WorkspaceIdentity);
        Assert.Equal(before.CommittedGeneration, owner.ReadCachedSnapshot()!.CommittedGeneration);
        Assert.Equal(1, fixture.Opens);
        Assert.Equal(0, fixture.Sessions[0].Closes);
        Assert.All(directory.Zip(fixture.Record.Fans), pair => Assert.Same(pair.First, pair.Second));
        Assert.Equal(first.Select(reading => reading.Value?.Number),
            (await core.CollectAsync(fields)).Select(reading => reading.Value?.Number));
        Assert.Equal(1, fixture.Opens);
    }

    [Fact]
    public async Task FailureAndOverflowRetainDefinitionsAndClearValuesWithoutPartialDirectory()
    {
        var fixture = new NativeRecording();
        using var owner = fixture.Owner();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        var fields = IntelFanConsumer.Fields(await core.DiscoverAsync());
        await core.CollectAsync(fields);
        fixture.Sessions[0].Fans = [new(91), new(92)];
        fixture.FanCount = 17;
        Assert.Equal<FieldReference>(fields, IntelFanConsumer.Fields(await core.DiscoverAsync()));
        var values = await core.CollectAsync(fields);
        Assert.All(values, reading => { Assert.Null(reading.Value); Assert.Null(reading.ObservedAt); });
        Assert.Equal(0, fixture.Sessions[0].Closes);
        Assert.Equal(1, fixture.Sessions[1].Closes);
        Assert.Equal(2, IntelFanConsumer.Fields(core.Catalog).Length);
        owner.Dispose();
        Assert.Equal(1, fixture.Sessions[0].Closes);
    }

    [Fact]
    public async Task FrozenDirectoryProbeDoesNotTouchTheHeldSdkSession()
    {
        var fixture = new NativeRecording();
        using var owner = fixture.Owner();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        var fields = IntelFanConsumer.Fields(await core.DiscoverAsync());
        var calls = fixture.Sessions[0].Operations;
        fixture.Record.Frozen = true;
        Assert.Equal<FieldReference>(fields, IntelFanConsumer.Fields(await core.DiscoverAsync()));
        Assert.Equal(calls, fixture.Sessions[0].Operations);
        Assert.Equal(1, fixture.Opens);
    }

    [Fact]
    public void RealHostHolderPreservesDirectoryAcrossProbeAndClosesOnlyAfterOldLeaseEnds()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var fixture = new NativeRecording();
        using var owner = fixture.Host(slots);
        using var workspaceHolding = owner.Hold();
        using var old = owner.Acquire();
        var identity = old.WorkspaceIdentity;
        var catalog = old.Workspace.ReadCatalog();
        owner.RequestTopologyRefresh();
        Assert.NotNull(owner.RefreshTopologyIfRequested());
        Assert.Equal(identity, owner.CurrentWorkspaceIdentity);
        Assert.Equal(1, fixture.Opens);
        using (var same = owner.Acquire()) Assert.Same(old.Workspace, same.Workspace);
        fixture.Sessions[0].Fans = [new(91), new(92)];
        owner.RequestTopologyRefresh();
        Assert.NotNull(owner.RefreshTopologyIfRequested());
        Assert.NotEqual(identity, owner.CurrentWorkspaceIdentity);
        Assert.Equal(0, fixture.Sessions[0].Closes);
        Assert.False(old.TryPublish(_ => true, out _));
        using (var current = owner.Acquire())
            Assert.DoesNotContain(current.Workspace.ReadCatalog().SystemFanIdentities,
                fan => fan.IgclFan?.DirectoryId == catalog.SystemFanIdentities.First(fan => fan.IgclFan is not null).IgclFan!.DirectoryId);
        owner.Dispose();
        Assert.Equal(0, fixture.Sessions[0].Closes);
        Assert.Equal(1, fixture.Sessions[1].Closes);
        old.Dispose();
        Assert.Equal(1, fixture.Sessions[0].Closes);
    }

    [Fact]
    public async Task OriginalHostWorkerUsesTheSameIgclZoneAndOnlyTheRequestedSessionFan()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var fixture = new NativeRecording();
        using var owner = fixture.Host(slots);
        using var workspaceHolding = owner.Hold();
        var dashboard = new HardwareMetricCatalogState();
        IntelGclFanIdentity fan;
        using (var lease = owner.Acquire())
        {
            fan = lease.Workspace.ReadCatalog().SystemFanIdentities.Last(identity => identity.IgclFan is not null).IgclFan!;
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        }
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, fan.ExactIdentityKey, [RecordedIntelFanResources.Name]);
        var zone = new IntelGclMonitoringZone(fixture.Reader);
        var registry = new MonitoringSourceZoneRegistry([new WindowsMemoryMonitoringZone(), zone]);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!, new WindowsGpuAdapterOrderMonitoringZone(RecordedIntelFanResources.Inventory),
            null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard,
            registry, slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance,
            intelGclZone: zone, borrowedControls: fixture.Resources.Controls);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Empty(fixture.Sessions[0].Reads);
        _ = sampler.ReadLatest(request);
        Assert.Empty(fixture.Sessions[0].Reads);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using var stream = sampler.SubscribeAsync("intel-fan-recorded", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync());
            var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(stream.Current, fan.ExactIdentityKey,
                new HashSet<string> { RecordedIntelFanResources.Name })).Fields);
            Assert.Equal(1450d, field.NumericValue);
            Assert.All(fixture.Sessions[0].Reads, handle => Assert.Equal(new IntPtr(12), handle));
            Assert.Equal(1, fixture.Opens);
        }
        finally { await sampler.StopAsync(default); }
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void IntelFanBudgetUsesOriginalTotalCompilerCapacity(int count, bool accepted)
    {
        var error = Record.Exception(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var rules = root["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
            rules.Single(rule => rule!["template_id"]!.GetValue<string>() == "fan.igclRpm")!["maximum_instance_count"] = count;
        }));
        if (accepted) Assert.Null(error); else Assert.IsType<InvalidDataException>(error);
    }

    private sealed class NativeRecording
    {
        internal readonly RecordedIntelFanResources Record = new();
        internal readonly IntelGclReader Reader;
        internal readonly WindowsHardwareMetricResources Resources;
        internal readonly List<Session> Sessions = [];
        internal int Opens, FanCount = 2;
        internal NativeRecording()
        {
            Reader = new IntelGclReader(() =>
            {
                Opens++;
                var session = new Session { Fans = Enumerable.Range(0, FanCount).Select(index => new IntPtr(11 + index)).ToArray() };
                Sessions.Add(session); return session;
            });
            Record.OpenOverride = (inventory, maximum, prior) =>
            {
                var result = WindowsIntelFanTopology.OpenDirectory((current, limit) => Reader.OpenFanDirectory(current, limit), inventory, maximum, prior);
                if (result.Status == NativeMetricSnapshotCatalogTopologyStatus.Complete) Record.Fans = result.Identities.ToArray();
                return result;
            };
            Record.ReadOverride = Reader.ReadFans;
            Resources = Record.Create();
        }
        internal WindowsHardwareMetricOwner Owner() => new(WindowsMetricConfiguration.Default, Resources, new(), Record.Mode, TimeSpan.Zero);
        internal HostManagerMetricSnapshotOwner Host(HostManagerSamplingSubscriptionTestFixture slots)
        {
            var topology = new WindowsHardwareMetricTopologySource(new(Resources.ReadGpuInventory, Resources.ReadStorageIdentities,
                Resources.ReadFanIdentities, Resources.ReadBatteryIdentities, Resources.ReadNetworkIdentities, Resources.ReadNumaIdentities)
                { OpenIgclFanDirectory = Resources.OpenIgclFanDirectory, ReadNvmlFanIdentities = Resources.ReadNvmlFanIdentities }, () => null);
            return new(slots.Provider, new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
                new HostManagerMetricSnapshotCatalogTopologySource((npu, intel, prior) => topology.Capture(npu, intel, prior)),
                new EnvironmentInfo(Path.Combine(Path.GetTempPath(), "igcl-fan-host-" + Guid.NewGuid().ToString("N"))),
                NullLogger<HostManagerMetricSnapshotOwner>.Instance, NativeMetricSnapshotScratch.CreateOrdinary);
        }
    }
    private sealed class Session : IIntelGclSession
    {
        internal IntPtr[] Fans = [new(11), new(12)];
        internal int Closes, Operations;
        // The sampler reads on its own thread while the test inspects the reads.
        private readonly List<IntPtr> reads = [];
        internal IntPtr[] Reads { get { lock (reads) return [.. reads]; } }
        public bool TryGetDevices(out IntPtr[] devices, CancellationToken token) { Operations++; devices = [new(1)]; return true; }
        public bool TryGetIdentity(IntPtr device, out IntelGclIdentity identity) { Operations++; identity = new(7, 0x8086, 0x56A0); return true; }
        public bool TryGetFans(IntPtr device, uint maximum, out IntPtr[] fans, CancellationToken token)
        { Operations++; fans = Fans.ToArray(); return fans.Length <= maximum; }
        public bool TryReadFanRpm(IntPtr fan, out int rpm) { Operations++; lock (reads) reads.Add(fan); rpm = fan == new IntPtr(11) ? 0 : 1450; return true; }
        public bool TryReadTelemetry(IntPtr device, out IntelGclPowerTelemetry value) { value = default; return false; }
        public bool TryReadPciState(IntPtr device, out IntelGclPciState value) { value = default; return false; }
        public bool TryReadPciProperties(IntPtr device, out IntelGclPciProperties value) { value = default; return false; }
        public void Dispose() { Assert.Equal(0, Closes); Closes++; }
    }
    private sealed class EnvironmentInfo(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
