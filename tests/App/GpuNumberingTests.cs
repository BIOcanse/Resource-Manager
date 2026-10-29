using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// User ruling 2026-10-27: GPU n is the n-th hardware adapter in the backend's DXGI enumeration, software adapters
/// skipped, independent of Task Manager's numbering; wherever a number is published its adapter name is available.
/// The recorded rows are this machine's DXGI order (NVIDIA, AMD, Microsoft Basic Render), whose LUID order and PCI bus
/// order disagree with each other, so a consumer that re-sorts by either fails one of the two composition cases.
/// </summary>
public sealed class GpuNumberingTests
{
    private const string NvidiaName = "NVIDIA GeForce RTX 5060 Laptop GPU";
    private const string AmdName = "AMD Radeon(TM) 610M";
    private const string NvidiaPnp = @"PCI\VEN_10DE&DEV_2D19\NVIDIA";
    private const string AmdPnp = @"PCI\VEN_1002&DEV_164E\AMD";
    private const uint NvidiaLuid = 0x8D1FDFC9;
    private const uint AmdLuid = 0x120D7;

    [Fact]
    public void ThisMachinesDxgiOrderNumbersNvidiaThenAmdAndSkipsBasicRender()
    {
        var rows = RecordedDxgiRows();
        var read = Reader(rows[0], rows[1], rows[2]).ReadInventory();
        Assert.Equal(SamplingObservationStatus.Current, read.Status);
        Assert.Equal(new[] { (0, NvidiaName), (1, AmdName) }, read.Adapters.Select(a => (a.Index, a.Name)));
        Assert.Equal(2U, read.ObservedCount);
        Assert.Equal(0U, read.SkippedCount); // A software adapter does not make the inventory incomplete.
    }

    [Fact]
    public void NumbersFollowTheDxgiOrderInsteadOfKindVendorLuidOrPciBus()
    {
        var rows = RecordedDxgiRows();
        var read = Reader(rows[1], rows[0]).ReadInventory();
        Assert.Equal(new[] { (0, AmdName), (1, NvidiaName) }, read.Adapters.Select(a => (a.Index, a.Name)));
    }

    [Fact]
    public void SoftwareAdapterBetweenHardwareAdaptersTakesNoNumber()
    {
        var rows = RecordedDxgiRows();
        var read = Reader(rows[0], rows[2], rows[1]).ReadInventory();
        Assert.Equal(new[] { (0, NvidiaName), (1, AmdName) }, read.Adapters.Select(a => (a.Index, a.Name)));
    }

    [Fact]
    public void AdapterDroppedForAnUnusableLuidLeavesItsNumberUnused()
    {
        var rows = RecordedDxgiRows();
        var read = Reader(rows[0] with { Luid = default }, rows[1]).ReadInventory();
        Assert.Equal(1U, read.SkippedCount);
        var remaining = Assert.Single(read.Adapters);
        Assert.Equal((1, AmdName), (remaining.Index, remaining.Name));
    }

    [Fact]
    public void UnavailableEnumerationPublishesNoAdapters()
    {
        var read = new WindowsGpuAdapterOrderReader(() => new(SamplingObservationStatus.Unavailable, 1, []),
            _ => throw new InvalidOperationException("Identity must not be queried.")).ReadInventory();
        Assert.Equal(SamplingObservationStatus.Unavailable, read.Status);
        Assert.Empty(read.Adapters);
        Assert.Equal(1U, read.SkippedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryConsumerKeepsTheReadNumberAndCanPairItWithTheAdapterName(bool amdFirst)
    {
        var rows = RecordedDxgiRows();
        var zone = new WindowsGpuAdapterOrderMonitoringZone(amdFirst
            ? Reader(rows[1], rows[2], rows[0]).ReadInventory
            : Reader(rows[0], rows[1], rows[2]).ReadInventory);
        var expected = amdFirst
            ? new[] { (Index: 0, Name: AmdName, Luid: (ulong)AmdLuid), (Index: 1, Name: NvidiaName, Luid: (ulong)NvidiaLuid) }
            : new[] { (Index: 0, Name: NvidiaName, Luid: (ulong)NvidiaLuid), (Index: 1, Name: AmdName, Luid: (ulong)AmdLuid) };
        var inventory = zone.ReadInventory(true);
        Assert.Equal(expected.Select(e => (e.Index, e.Name)), inventory.Adapters.Select(a => (a.Index, a.Name)));

        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, Resources(zone), new());
        await owner.CaptureAsync(MetricSampleRequest.ForIds(["gpu.0.usage", "gpu.1.usage", MetricDatasetIds.SystemGpuInventory]).WithCatalog());
        var snapshot = owner.ReadCachedSnapshot()!;

        // Values land on the number of the adapter they were read from (PDH by LUID).
        foreach (var gpu in expected)
            Assert.Equal(gpu.Name == NvidiaName ? 73 : 19, snapshot.Items[$"gpu.{gpu.Index}.usage"].NumericValue);
        // Snapshot GPUs feed the performance-score endpoint, the control catalog and placement names;
        // the scheduling inventory carries the number with the adapter LUID.
        Assert.Equal(expected.Select(e => (e.Index, e.Name)), snapshot.Gpus.OrderBy(g => g.Index).Select(g => (g.Index, g.Name)));
        Assert.Equal(expected.Select(e => (e.Index, e.Luid)),
            snapshot.GpuInventory.Adapters.OrderBy(a => a.Index).Select(a => (a.Index, a.AdapterKey)));

        // The frontend pairs gpu.N.* with a name through the metric scope key and the field catalog's gpu_adapter device.
        var catalog = MetricCatalog.FromSnapshot(snapshot);
        var devices = MetricDeviceFields.Catalog(snapshot).Where(d => d.Device.Kind == "gpu_adapter").ToArray();
        Assert.Equal(expected.Length, devices.Length);
        foreach (var gpu in expected)
        {
            var metric = Assert.Single(catalog, m => m.Id == $"gpu.{gpu.Index}.usage");
            Assert.Equal("gpu", metric.ScopeKind);
            var device = Assert.Single(devices, d => d.DeviceIdentity == metric.ScopeKey);
            Assert.Equal(gpu.Name, device.Device.DisplayName);
            Assert.Equal(gpu.Index, device.Device.Ordinal);
        }

        var placement = GpuPlacementTargetInventoryProjection.Create(snapshot, []);
        Assert.Equal(expected.Select(e => ($"gpu{e.Index}", (string?)e.Name)), placement.ExactTargets.Select(t => (t.TargetGpu, t.AdapterName)));
        foreach (var adapter in inventory.Adapters)
        {
            var shim = D3d11ProxyShimRuntime.ResolveTarget(new("target", "target", $"gpu:{adapter.Index}", false), inventory.Adapters);
            Assert.Equal((adapter.Index, adapter.Name), (shim.AdapterIndex!.Value, shim.AdapterName!));
            // Vendor libraries enumerate in their own order; they bind to the inventory adapter by identity.
            var vendor = WindowsGpuProviderIdentityResolver.ResolvePnp(inventory, adapter.PhysicalIdentities[0].PnpInstanceId);
            Assert.True(vendor.IsCurrent);
            Assert.Same(adapter, vendor.Binding.Adapter);
        }
    }

    [Fact]
    public void PdhPhysicalIndexZeroOnBothAdaptersMapsByLuidToTheInventoryNumbers()
    {
        var rows = RecordedDxgiRows();
        var inventory = Reader(rows[0], rows[1], rows[2]).ReadInventory();
        var pdh = new PdhGpuEngineUsageReader(new FixedPdh(new NativePdhSnapshot(DateTimeOffset.UtcNow, 1, 1,
        [
            new() { AdapterLuid = AmdLuid, PhysicalIndex = 0, EngineClass = NativePdhEngineClass.Compute, UsagePercent = 19 },
            new() { AdapterLuid = NvidiaLuid, PhysicalIndex = 0, EngineClass = NativePdhEngineClass.Compute, UsagePercent = 73 }
        ], [], default)));
        var usage = pdh.ReadUsageByAdapterIndex(inventory.Adapters);
        Assert.Equal(73, usage[0]);
        Assert.Equal(19, usage[1]);
    }

    private static WindowsGpuAdapterOrderReader Reader(params WindowsGpuAdapter[] dxgiOrder)
        => new(() => new(SamplingObservationStatus.Current, 0, dxgiOrder), static adapter => adapter);

    /// <summary>This machine's DXGI EnumAdapters1 order: NVIDIA (PCI bus 1), AMD integrated (bus 5), Basic Render (software).</summary>
    private static WindowsGpuAdapter[] RecordedDxgiRows()
    {
        var fixture = NvidiaFanFixture.Adapter(0, NvidiaLuid, NvidiaPnp);
        var nvidia = fixture with
        {
            Name = NvidiaName, DeviceId = 0x2D19,
            PhysicalIdentities = [fixture.PhysicalIdentities[0] with { DeviceId = 0x2D19, PciBus = 1 }]
        };
        var amd = NvidiaFanFixture.Adapter(1, AmdLuid, AmdPnp) with
        {
            Name = AmdName, VendorId = 0x1002, DeviceId = 0x164E, DedicatedVideoMemoryBytes = 512UL << 20,
            Kind = WindowsGpuAdapterKind.Integrated,
            PhysicalIdentities = [fixture.PhysicalIdentities[0] with { PnpInstanceId = AmdPnp, VendorId = 0x1002, DeviceId = 0x164E, PciBus = 5 }]
        };
        var basicRender = new WindowsGpuAdapter(2, "Microsoft Basic Render Driver", 0x1414, 0x8C, 0,
            new() { LowPart = 0x120A2 }, true, 0, WindowsGpuAdapterKind.Unknown);
        return [nvidia, amd, basicRender];
    }

    private sealed class FixedPdh(NativePdhSnapshot snapshot) : INativePdhSnapshotSource
    {
        public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
            => new(snapshot, NativePdhProviderAvailability.Available, NativePdhResultCode.Ok, 0);
    }

    private static WindowsHardwareMetricResources Resources(WindowsGpuAdapterOrderMonitoringZone zone) => new()
    {
        Controls = new() { ControlReadCpuTopology = () => null! },
        ReadCpuCounters = () => throw new InvalidOperationException(), ReadMemory = () => throw new InvalidOperationException(),
        ReadVirtualMemory = () => throw new InvalidOperationException(), ReadNumaAvailable = (_, _) => new Dictionary<string, ulong>(),
        ReadNumaIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadGpuInventory = () => zone.ReadInventory(true),
        ReadCpuFrequency = () => throw new InvalidOperationException(),
        ReadGpuUsage = adapters => new(adapters.ToDictionary(a => a.Index, a => a.Name == NvidiaName ? 73d : 19d),
            NativePdhProviderAvailability.Available, DateTimeOffset.UtcNow),
        ReadNvmlAsync = (_, _, _) => Task.FromResult<IReadOnlyList<GpuMetrics>>([]), ReadNvmlFanIdentities = _ => [],
        OpenIgclFanDirectory = (_, _, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(), ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(),
        ReadAmdSmu = _ => throw new InvalidOperationException(), ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(),
        ReadIntelMsr = (_, _) => throw new InvalidOperationException(),
        ReadBatteryIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadBattery = (_, _) => throw new InvalidOperationException(),
        ReadNetworkIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(), ReadSystemIo = _ => throw new InvalidOperationException(),
        ReadStorageIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadStorageSensors = () => throw new InvalidOperationException(), ReadNvmeSensors = _ => throw new InvalidOperationException(),
        ReadAtaSensors = _ => throw new InvalidOperationException(), ReadHardwareMonitor = () => throw new InvalidOperationException(),
        ReadNotebookFans = () => throw new InvalidOperationException(), SynchronizeNotebookDemand = _ => { }
    };
}
