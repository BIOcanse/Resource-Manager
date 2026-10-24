using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricGpuTopologyIsolationTests
{
    [Theory]
    [InlineData("inventory", false)]
    [InlineData("identity", false)]
    [InlineData("exception", false)]
    [InlineData("inventory", true)]
    [InlineData("identity", true)]
    [InlineData("exception", true)]
    public async Task HostRefreshPublishesIndependentCurrentValuesAndSettlesTicket(string failure, bool replaceIndependentDevice)
    {
        var recording = new Recording();
        using var resources = recording.Resources();
        var source = recording.Topology();
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(source.Capture());
        var current = new LastSuccessfulHardwareMetricSnapshot();
        var capture = new WindowsHardwareMetricCapture(resources, _ => NativeMetricSnapshotZoneMode.Normal,
            TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), fixture.Owner.RequestTopologyRefresh);
        using (var initial = fixture.Owner.Acquire())
        {
            for (var phase = 0; phase < 3; phase++)
            {
                recording.Failure = phase == 1 ? failure : null;
                recording.Value = 30 + phase * 10;
                recording.DiskIdentity = replaceIndependentDevice && phase != 0 ? "disk-replacement" : "disk-recorded";
                fixture.Topology = source.Capture();
                fixture.Owner.RequestTopologyRefresh();
                var refreshed = Assert.IsType<NativeMetricSnapshotCatalogTopologySnapshot>(fixture.Owner.RefreshTopologyIfRequested());
                AssertTopology(refreshed, phase == 1, failure);
                var reads = fixture.TopologyReads;
                Assert.Null(fixture.Owner.RefreshTopologyIfRequested());
                Assert.Equal(reads, fixture.TopologyReads);
                using var lease = fixture.Owner.Acquire();
                if (replaceIndependentDevice && phase != 0) Assert.NotEqual(initial.WorkspaceIdentity, lease.WorkspaceIdentity);
                else Assert.Equal(initial.WorkspaceIdentity, lease.WorkspaceIdentity);
                Assert.Equal(initial.Workspace.ReadCatalog().GpuIdentities.ToArray(), lease.Workspace.ReadCatalog().GpuIdentities.ToArray());
                var request = Request(lease.Workspace.ReadCatalog());
                var snapshot = await capture.CaptureUnderGateAsync(request, lease.Workspace, lease.WorkspaceIdentity,
                    lease.Topology, commit => lease.TryPublish(_ => { commit(); return true; }, out _), new("recorded"), default,
                    onCommitted: completed => current.Publish(completed, request));
                Assert.NotNull(snapshot);
                AssertPublished(current.Read()!, request, recording.Value, phase == 1);
            }
        }
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("identity")]
    [InlineData("exception")]
    public async Task StandaloneRefreshEmptiesGpuWithoutBlockingOtherValuesAndRecovers(string failure)
    {
        var recording = new Recording();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Resources(), new(),
            unavailableCooldown: TimeSpan.Zero);
        var request = Request(WindowsMetricCatalogTopologyPublication.ProjectCatalog(
            WindowsMetricConfiguration.Default.CatalogConfiguration, recording.Topology().Capture(), NativeMetricSnapshotCatalogHandleMap.Empty));
        for (var phase = 0; phase < 3; phase++)
        {
            recording.Failure = phase == 1 ? failure : null;
            recording.Value = 30 + phase * 10;
            await owner.CaptureAsync(request);
            AssertPublished(owner.ReadCachedSnapshot()!, request, recording.Value, phase == 1);
        }
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("identity")]
    [InlineData("exception")]
    public async Task ColdStartWithUnavailableGpuStillDiscoversAndPublishesOtherDevices(string failure)
    {
        var recording = new Recording { Failure = failure };
        var topology = recording.Topology().Capture();
        AssertTopology(topology, true, failure);
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(topology);
        using var lease = fixture.Owner.Acquire();
        Assert.Empty(lease.Workspace.ReadCatalog().GpuScopeBindings);
        Assert.Single(lease.Workspace.ReadCatalog().StorageIdentities);
        Assert.Single(lease.Workspace.ReadCatalog().SystemFanIdentities);
        Assert.Single(lease.Workspace.ReadCatalog().BatteryIdentities);
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Resources(), new());
        var request = Request(lease.Workspace.ReadCatalog());
        var snapshot = await owner.CaptureAsync(request);
        AssertPublished(snapshot, request, recording.Value, true);
        recording.Failure = null;
        var recovered = await owner.CaptureAsync(request);
        Assert.Equal(SamplingObservationStatus.Current, recovered.Datasets[MetricDatasetIds.SystemMetricCatalog].Status);
        Assert.Contains(recovered.FieldBindings.Values, binding => binding.Field.Resource == MetricResource.Gpu);
    }

    private static void AssertTopology(NativeMetricSnapshotCatalogTopologySnapshot topology, bool failed, string failure)
    {
        Assert.Equal(failed ? NativeMetricSnapshotCatalogTopologyStatus.Unavailable : NativeMetricSnapshotCatalogTopologyStatus.Complete, topology.GpuStatus);
        Assert.Equal(failed ? failure == "identity" ? "gpu-identity-unavailable" : "gpu-inventory-unavailable" : null, topology.GpuFailureCode);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, topology.StorageStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, topology.FanStatus);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, topology.BatteryStatus);
        Assert.Single(topology.StorageSensors); Assert.Single(topology.SystemFans); Assert.Single(topology.Batteries);
        if (failed) Assert.Empty(topology.GpuAdapters);
    }

    private static MetricSampleRequest Request(NativeMetricSnapshotCatalogProjection catalog)
        => MetricSampleRequest.ForIds(catalog.MetricBindings.Values.Where(binding => binding.FieldCode is
                MetricFieldCode.DiskTemperature or MetricFieldCode.FanRpm or MetricFieldCode.BatteryChargePercent or MetricFieldCode.GpuUsage)
            .Select(static binding => binding.MetricId)
            .Concat([MetricDatasetIds.StorageInventory, MetricDatasetIds.FanInventory, MetricDatasetIds.BatteryInventory])).WithCatalog();

    private static void AssertPublished(HardwareMetricSnapshot snapshot, MetricSampleRequest request, int value, bool failed)
    {
        foreach (var code in new[] { MetricFieldCode.DiskTemperature, MetricFieldCode.FanRpm, MetricFieldCode.BatteryChargePercent })
        {
            var binding = Assert.Single(snapshot.FieldBindings.Values, binding => binding.Field.Code == code && request.Includes(binding.RequestAddress));
            Assert.Equal(SamplingObservationStatus.Current, snapshot.Datasets[binding.RequestAddress].Status);
            Assert.Equal((double)value, snapshot.Items[binding.RequestAddress].NumericValue);
        }
        var gpu = snapshot.FieldBindings.Values.Where(binding => binding.Field.Code == MetricFieldCode.GpuUsage).ToArray();
        if (!failed) Assert.NotEmpty(gpu);
        foreach (var binding in gpu)
        {
            Assert.Equal(failed ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, snapshot.Datasets[binding.RequestAddress].Status);
            Assert.Equal(failed ? null : (double?)value, snapshot.Items.GetValueOrDefault(binding.RequestAddress)?.NumericValue);
            if (failed)
            {
                var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, binding.DeviceIdentity,
                    new HashSet<string> { binding.Field.Name })).Fields);
                Assert.Equal(SamplingObservationStatus.Unavailable, field.Status);
                Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue);
            }
        }
        Assert.Equal(failed ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, snapshot.Datasets[MetricDatasetIds.SystemMetricCatalog].Status);
        if (failed) Assert.StartsWith("gpu-", snapshot.Datasets[MetricDatasetIds.SystemMetricCatalog].FailureCode);
        Assert.Equal(SamplingObservationStatus.Current, snapshot.Datasets[MetricDatasetIds.StorageInventory].Status);
        Assert.Equal(SamplingObservationStatus.Current, snapshot.Datasets[MetricDatasetIds.FanInventory].Status);
        Assert.Equal(SamplingObservationStatus.Current, snapshot.Datasets[MetricDatasetIds.BatteryInventory].Status);
    }

    private sealed class Recording
    {
        internal string? Failure;
        internal int Value = 30;
        internal string DiskIdentity = "disk-recorded";
        private readonly BatteryCatalogIdentity battery = new(0, "battery-path", "battery-id", new(0, "LION", 100, 100, 1));
        private readonly WindowsGpuAdapter adapter = NvidiaFanFixture.Adapter(0, 0xA1, NvidiaFanFixture.ParentA);
        private WindowsGpuAdapterInventoryRead Inventory()
            => Failure == "exception" ? throw new InvalidOperationException("recorded GPU inventory exception")
                : new(Failure == "inventory" ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current,
                    Failure == "inventory" ? 0UL : 1UL, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1,
                    [Failure == "identity" ? adapter with { PhysicalIdentityStatus = SamplingObservationStatus.Unavailable, PhysicalIdentities = [] } : adapter]);
        private NativeMetricSnapshotOrdinalTopologyRead Storage(ulong generation) => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [new(0, DiskIdentity, generation)]);
        private NativeMetricSnapshotOrdinalTopologyRead Fans(ulong generation) => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [new(0, "fan-recorded", generation)]);
        internal WindowsHardwareMetricTopologySource Topology() => new(new(Inventory, Storage, Fans,
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [battery]),
            () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []))
            { ReadNvmlFanIdentities = _ => [] }, () => null);
        internal WindowsHardwareMetricResources Resources() => new()
        {
            Controls = new() { ControlReadCpuTopology = () => null! },
            CpuName = "recorded", MemoryDescription = "recorded",
            ReadCpuCounters = () => throw new InvalidOperationException(), ReadMemory = () => throw new InvalidOperationException(),
            ReadVirtualMemory = () => throw new InvalidOperationException(), ReadNumaAvailable = (_, _) => new Dictionary<string, ulong>(),
            ReadNumaIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadGpuInventory = Inventory,
            ReadCpuFrequency = () => throw new InvalidOperationException(),
            ReadGpuUsage = _ => new(new Dictionary<int, double> { [0] = Value }, NativePdhProviderAvailability.Available, DateTimeOffset.UtcNow),
            ReadNvmlAsync = (_, _, _) => Task.FromResult<IReadOnlyList<GpuMetrics>>([new(0, "recorded GPU", Value,
                0, 0, 0, 0, 0, 0, 0, new(new("recorded", "Active", null), null, null, null, null, null, null, null, null, null, null))]),
            ReadNvmlFanIdentities = _ => [],
            OpenIgclFanDirectory = (_, _, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(), ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(),
            ReadAmdSmu = _ => throw new InvalidOperationException(), ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(),
            ReadIntelMsr = (_, _) => throw new InvalidOperationException(),
            ReadBatteryIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [battery]),
            ReadBattery = (_, _) => [new(battery.ExactIdentityKey, new Dictionary<MetricFieldCode, double> { [MetricFieldCode.BatteryChargePercent] = Value }, null, false)],
            ReadNetworkIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(),
            ReadSystemIo = _ => throw new InvalidOperationException(), ReadStorageIdentities = Storage, ReadFanIdentities = Fans,
            ReadStorageSensors = () => new(new("recorded", "Active", null), [new(0, "disk", Value, "recorded") { IdentityKey = DiskIdentity }]),
            ReadNvmeSensors = _ => throw new InvalidOperationException(), ReadAtaSensors = _ => throw new InvalidOperationException(),
            ReadHardwareMonitor = () => new(new("recorded", "Active", null), null, null, null, null, null, null, null,
                [new(0, "fan", Value, null, "recorded") { IdentityKey = "fan-recorded" }], []) { CatalogComplete = true },
            ReadNotebookFans = () => throw new InvalidOperationException(), SynchronizeNotebookDemand = _ => { }
        };
    }
}
