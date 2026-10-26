using System.Collections.Immutable;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class IntelPsysOwnerIntegrationTests
{
    private const string Raw = "system_intel_psys_energy_counter_raw";
    private const string Unit = "system_intel_psys_energy_unit_joules";
    private const string Identity = "windows-intel-psys-platform|windows-cpu-package|1:1";

    [Fact]
    public async Task OriginalOwnerKeepsOnePlatformDeclarationClearsUnavailableWithoutQueryAndRetiresSuccessfulInapplicability()
    {
        var probe = new IntelMsrThermalCollectorTests.PsysOwnerProbe();
        using var owner = probe.Owner();
        var backend = owner.CreateBackend();
        Assert.Empty(backend.Catalog.Fields);
        Assert.Equal(0, probe.Opens);
        var catalog = await backend.DiscoverAsync(default);
        var fields = PsysFields(catalog);
        Assert.Equal(2, fields.Length);
        Assert.All(fields, field => Assert.Equal(Identity, field.DeviceIdentity));
        Assert.Equal(0, probe.Opens);
        using var borrow = owner.BorrowSubscriptionCapture();
        borrow.Bind(fields);
        var first = await borrow.CollectAsync(fields, new Permit());
        Assert.True(first.Committed);
        Assert.Equal(MetricInteger.Parse("0"), first.Readings.Single(static reading => reading.Field.FieldName == Raw).Value!.Integer);
        var reads = probe.Reads;
        var calls = probe.DelegateCalls;
        probe.UnavailableTopology = true;
        _ = await backend.DiscoverAsync(default);
        Assert.Equal(2, PsysFields(backend.Catalog).Length);
        var failed = await borrow.CollectAsync(fields, new Permit());
        Assert.True(failed.Committed);
        Assert.Equal(reads, probe.Reads);
        Assert.Equal(calls, probe.DelegateCalls);
        Assert.All(failed.Readings, reading =>
        {
            Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
            Assert.Null(reading.Value);
            Assert.Null(reading.ObservedAt);
        });
        Assert.All(fields, field => Assert.Null(backend.ReadCached(field).Value));
        probe.UnavailableTopology = false;
        probe.UnsupportedTopology = true;
        _ = await backend.DiscoverAsync(default);
        Assert.Empty(PsysFields(backend.Catalog));
        var retired = await borrow.CollectAsync(fields, new Permit());
        Assert.True(retired.Committed);
        Assert.All(retired.Readings, reading => Assert.Null(reading.Value));
        Assert.Equal(reads, probe.Reads);
        Assert.Equal(calls, probe.DelegateCalls);
    }

    [Fact]
    public async Task DirectCacheBorrowFreezeAndCanceledLateReadUseOriginalCurrentWithoutAnotherClockOrOwner()
    {
        var probe = new IntelMsrThermalCollectorTests.PsysOwnerProbe();
        var frozen = false;
        using var owner = probe.Owner(_ => frozen ? NativeMetricSnapshotZoneMode.Freeze : NativeMetricSnapshotZoneMode.Normal);
        var backend = owner.CreateBackend();
        var fields = PsysFields(await backend.DiscoverAsync(default));
        var raw = fields.Single(static field => field.FieldName == Raw);
        var unit = fields.Single(static field => field.FieldName == Unit);
        var first = Assert.Single(await backend.CollectAsync([raw], default));
        Assert.Equal(MetricInteger.Parse("0"), first.Value!.Integer);
        Assert.Equal(new uint[] { 0x64D }, probe.Registers);
        Assert.Equal(first, backend.ReadCached(raw));
        Assert.Equal(1, probe.Opens);
        Assert.Equal(probe.Opens, probe.Disposals);
        var reads = probe.Reads;
        _ = owner.ReadCachedSnapshot();
        _ = backend.Catalog;
        _ = backend.ReadCached(raw);
        Assert.Equal(reads, probe.Reads);
        probe.RawValue = ulong.MaxValue;
        var both = await backend.CollectAsync(fields, default);
        Assert.Equal(MetricInteger.Parse("4294967295"), both.Single(static reading => reading.Field.FieldName == Raw).Value!.Integer);
        Assert.Equal(Math.ScaleB(1d, -14), both.Single(static reading => reading.Field.FieldName == Unit).Value!.Number);
        Assert.All(probe.Addresses, address => Assert.Equal(new CpuProcessorAddress(1, 1), address));
        using var borrow = owner.BorrowSubscriptionCapture();
        borrow.Bind(fields);
        var previous = owner.ReadCachedSnapshot();
        var acceptedRaw = backend.ReadCached(raw);
        probe.RawValue = 17;
        Assert.False((await borrow.CollectAsync([raw], new Permit(allowPublish: false))).Committed);
        Assert.Equivalent(previous, owner.ReadCachedSnapshot(), strict: true);
        _ = await backend.CollectAsync([unit], default);
        Assert.Equal(acceptedRaw, backend.ReadCached(raw));
        previous = owner.ReadCachedSnapshot();
        probe.RawValue = 23;
        using var cancellation = new CancellationTokenSource();
        probe.AfterRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.CollectAsync([raw], new Permit(), cancellation.Token).AsTask());
        Assert.Equivalent(previous, owner.ReadCachedSnapshot(), strict: true);
        probe.AfterRead = null;
        probe.RawValue = 31;
        using var admissionCancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.CollectAsync([raw],
            new Permit(beforePublish: admissionCancellation.Cancel), admissionCancellation.Token).AsTask());
        Assert.Equivalent(previous, owner.ReadCachedSnapshot(), strict: true);
        var displayed = fields.ToDictionary(static field => field, backend.ReadCached);
        frozen = true;
        var opens = probe.Opens;
        var retained = await backend.CollectAsync(fields, default);
        Assert.All(retained, reading =>
        {
            Assert.Equal(displayed[reading.Field].Value, reading.Value);
            Assert.Equal(displayed[reading.Field].ObservedAt, reading.ObservedAt);
            Assert.Equal(displayed[reading.Field].Status, reading.Status);
        });
        Assert.Equal(opens, probe.Opens);
        frozen = false;
        var recovered = Assert.Single(await backend.CollectAsync([unit], default));
        Assert.Equal(Math.ScaleB(1d, -14), recovered.Value!.Number);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(probe.Opens, probe.Disposals);
    }

    [Fact]
    public async Task OriginalNativeSubscriptionSharesCurrentAndStopLeavesExplicitCollectionAvailable()
    {
        var probe = new IntelMsrThermalCollectorTests.PsysOwnerProbe();
        using var owner = probe.Owner();
        var backend = owner.CreateBackend();
        var raw = PsysFields(await backend.DiscoverAsync(default)).Single(static field => field.FieldName == Raw);
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected PSys subscription failure", failure.Error), TimeSpan.FromMilliseconds(1));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var stream = composition.SubscribeAsync("psys-platform", [raw], TimeSpan.FromMilliseconds(100),
            timeout.Token).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(MetricInteger.Parse("0"), Assert.Single(stream.Current).Value!.Integer);
            probe.RawValue = uint.MaxValue;
            FieldReading delivered;
            do
            {
                Assert.True(await stream.MoveNextAsync());
                delivered = Assert.Single(stream.Current);
            } while (delivered.Value?.Integer != MetricInteger.Parse("4294967295"));
            Assert.Equal(delivered.Value, backend.ReadCached(raw).Value);
        }
        await composition.StopAsync();
        var opens = probe.Opens;
        Assert.Equal(MetricInteger.Parse("4294967295"), backend.ReadCached(raw).Value!.Integer);
        Assert.Equal(opens, probe.Opens);
        Assert.Equal(MetricInteger.Parse("4294967295"), Assert.Single(await backend.CollectAsync([raw], default)).Value!.Integer);
        Assert.Equal(probe.Opens, probe.Disposals);
    }

    [Fact]
    public async Task RealHostWorkerUsesSamePlatformAndRejectsPublicationFromTheRetiredDirectory()
    {
        var probe = new IntelMsrThermalCollectorTests.PsysOwnerProbe();
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        // Runtime evidence stays inside the test output/workset. Its registered
        // outer validation batch owns retention; this fixture never deletes trees.
        var root = Path.Combine(AppContext.BaseDirectory, "psys-host-runtime", Guid.NewGuid().ToString("N"));
        var topology = probe.Topology();
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => topology.Capture()), new TestEnvironment(root),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(default);
        var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var zone = new IntelMsrMonitoringZone(probe.Reader);
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("PSys cannot read NUMA"),
            () => throw new InvalidOperationException("PSys cannot read global memory"));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner,
            dashboard, registry, slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, intelMsrZone: zone);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, Identity, [Raw, Unit]);
        Assert.Equal(2, request.Ids.Count);
        _ = await sampler.GetSnapshotAsync(request, default);
        Assert.Equal(0, probe.Opens);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sampler.StartAsync(timeout.Token);
        try
        {
            using (sampler.AcquireSubscription("psys-host", request, TimeSpan.FromMinutes(1)))
            {
                var current = await HardwareMetricCurrentTestWait.ReadAsync(sampler, request, timeout.Token);
                var fields = Assert.Single(MetricDeviceFields.Read(current, Identity)).Fields;
                Assert.Equal(MetricInteger.Parse("0"), fields.Single(static field => field.Name == Raw).IntegerValue);
                Assert.Equal(Math.ScaleB(1d, -14), fields.Single(static field => field.Name == Unit).NumericValue);
            }
            await sampler.StopAsync(default);
            var opens = probe.Opens;
            _ = sampler.ReadLatest(request);
            _ = await sampler.GetSnapshotAsync(request, default);
            Assert.Equal(opens, probe.Opens);
            Assert.Equal(probe.Opens, probe.Disposals);
            Assert.Equal(0, probe.Writes);
            probe.UnavailableTopology = true;
            owner.RequestTopologyRefresh();
            var failed = await sampler.CaptureSnapshotAsync(request, default);
            Assert.All(Assert.Single(MetricDeviceFields.Read(failed, Identity)).Fields, field =>
            { Assert.Null(field.IntegerValue); Assert.Null(field.NumericValue); });
            Assert.Equal(opens, probe.Opens);
            Assert.Null(owner.RefreshTopologyIfRequested()); // The failed optional directory ticket was handled.
            var stillFailed = await sampler.CaptureSnapshotAsync(request, default);
            Assert.All(Assert.Single(MetricDeviceFields.Read(stillFailed, Identity)).Fields, field =>
            { Assert.Null(field.IntegerValue); Assert.Null(field.NumericValue); });
            Assert.Equal(opens, probe.Opens);
            probe.UnavailableTopology = false;
            owner.RequestTopologyRefresh();
            var recovered = await sampler.CaptureSnapshotAsync(request, default);
            Assert.Equal(MetricInteger.Parse("0"), Assert.Single(MetricDeviceFields.Read(recovered, Identity)).Fields
                .Single(static field => field.Name == Raw).IntegerValue);
            Assert.Equal(opens + 1, probe.Opens);
            using var retired = owner.Acquire();
            probe.UnsupportedTopology = true;
            owner.RequestTopologyRefresh();
            Assert.NotNull(owner.RefreshTopologyIfRequested());
            Assert.NotEqual(retired.WorkspaceIdentity, owner.CurrentWorkspaceIdentity);
            var published = false;
            Assert.False(retired.TryPublish(_ => { published = true; return true; }, out _));
            Assert.False(published);
        }
        finally { await sampler.StopAsync(default); }
    }

    private static ImmutableArray<FieldReference> PsysFields(ReadWriteCatalog catalog)
        => [.. catalog.Fields.Where(static field => field.Field.FieldName is Raw or Unit).Select(static field => field.Field)];
    private sealed class Permit(bool active = true, bool allowPublish = true, Action? beforePublish = null) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit)
        { if (!active || !allowPublish) return false; beforePublish?.Invoke(); commit(); return true; }
    }
    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed partial class IntelMsrThermalCollectorTests
{
    // This adapter exposes the existing recorded CPUID/MSR seam to the independent
    // owner integration tests. It adds no second register reader or sampling loop.
    internal sealed class PsysOwnerProbe
    {
        private readonly Access access = new() { Signature = 0x406E0, ThermalFlags = 0 };
        private readonly Hardware hardware;
        internal PsysOwnerProbe()
        {
            hardware = new(access);
            hardware.RaplRegisters[0x64D] = 0;
            Reader = new(() => { hardware.Opens++; return hardware; }, () => access);
        }
        internal IntelMsrReader Reader { get; }
        internal bool UnavailableTopology;
        internal bool UnsupportedTopology;
        internal ulong RawValue { set => hardware.RaplRegisters[0x64D] = value; }
        internal Action? AfterRead { set => hardware.AfterRead = value; }
        internal int Opens => hardware.Opens;
        internal int Disposals => hardware.Disposals;
        internal int Writes => hardware.Writes;
        internal int Reads => hardware.Reads.Count;
        internal int DelegateCalls;
        internal uint[] Registers => hardware.Reads.Select(static read => read.Register).ToArray();
        internal CpuProcessorAddress[] Addresses => hardware.Reads.Select(static read => read.Address).ToArray();
        private CpuOsTopologyInformation ReadCpuTopology()
        {
            if (UnavailableTopology) return null!;
            var topologyAccess = new Access { Signature = UnsupportedTopology ? 0xF06E0U : 0x406E0U, ThermalFlags = 0 };
            var cpuid = WindowsCpuThreadCpuidReader.ReadRegisters(topologyAccess.Read);
            return new([new("recorded-core", 0, false, [65])], [])
            { Packages = [new("windows-cpu-package|1:1", [65])], ThreadCpuid = [new(65, cpuid)] };
        }
        private static WindowsGpuAdapterInventoryRead Inventory() => new(SamplingObservationStatus.Current,
            1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []);
        internal WindowsHardwareMetricTopologySource Topology() => new(new(Inventory,
            _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [])), ReadCpuTopology);
        internal WindowsHardwareMetricOwner Owner(Func<string, NativeMetricSnapshotZoneMode>? mode = null)
            => new(WindowsMetricConfiguration.Default, Resources(), new() { IntelModulePath = "recorded", PawnIoLibraryPath = "recorded" }, mode);
        private WindowsHardwareMetricResources Resources() => new()
        {
            Controls = new() { ControlReadCpuTopology = ReadCpuTopology },
            ReadCpuCounters = () => throw new InvalidOperationException(), ReadMemory = () => throw new InvalidOperationException(),
            ReadVirtualMemory = () => throw new InvalidOperationException(), ReadNumaAvailable = (_, _) => throw new InvalidOperationException(),
            ReadNumaIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadGpuInventory = Inventory,
            ReadCpuFrequency = () => throw new InvalidOperationException(), ReadGpuUsage = _ => throw new InvalidOperationException(),
            ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException(), ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(),
            ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(), ReadAmdSmu = _ => throw new InvalidOperationException(),
            ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(),
            ReadIntelMsr = (targets, requested) => { DelegateCalls++; return Reader.Read(targets, requested); },
            ReadBatteryIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadBattery = (_, _) => throw new InvalidOperationException(),
            ReadNetworkIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(),
            ReadSystemIo = _ => throw new InvalidOperationException(), ReadStorageIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadFanIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadStorageSensors = () => throw new InvalidOperationException(),
            ReadNvmeSensors = _ => throw new InvalidOperationException(), ReadAtaSensors = _ => throw new InvalidOperationException(),
            ReadHardwareMonitor = () => throw new InvalidOperationException(), ReadNotebookFans = () => throw new InvalidOperationException(),
            SynchronizeNotebookDemand = _ => { }
        };
    }
}
