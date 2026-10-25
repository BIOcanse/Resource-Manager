using static Resource_Manager_APP.Tests.StorageMetricSnapshotTestData;
using System.Buffers.Binary;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class StorageInformationCollectorTests
{
    internal const string Identity = "windows-storage|unique|DISK-A";
    private const ulong LargeCount = (1UL << 63) | 3;

    [Fact]
    public void NvmeQueryAndResponseRespectOffsetsUnitsAndEnduranceSemantics()
    {
        var request = WindowsNvmeHealthReader.CreateHealthQuery();
        Assert.Equal(560, request.Length);
        Assert.Equal(49U, U32(request, 0));
        Assert.Equal(3U, U32(request, 8));
        Assert.Equal(2U, U32(request, 12));
        Assert.Equal(2U, U32(request, 16));
        Assert.Equal(40U, U32(request, 24));
        Assert.Equal(512U, U32(request, 28));
        var response = Response();
        var health = WindowsNvmeHealthReader.ParseHealthResponse(response)!;
        Assert.Equal(255UL, health.IntegerValues[MetricFieldCode.NvmePercentageUsed]);
        Assert.Equal(0xA5UL, health.IntegerValues[MetricFieldCode.NvmeCriticalWarning]);
        Assert.Equal(42U, health.IntegerValues[MetricFieldCode.NvmeWarningTemperatureMinutes]);
        Assert.Equal(36.85, health.Values[MetricFieldCode.NvmeCompositeTemperature], 6);
        Assert.Equal(-3.15, health.Values[MetricFieldCode.NvmeTemperatureSensor8], 6);
        Assert.False(health.Values.ContainsKey(MetricFieldCode.NvmeTemperatureSensor1));
        response[48 + 3] = 101;
        Assert.False(WindowsNvmeHealthReader.ParseHealthResponse(response)!.IntegerValues.ContainsKey(MetricFieldCode.NvmeAvailableSpare));
    }

    [Theory]
    [InlineData(0, 47)]
    [InlineData(4, 49)]
    [InlineData(8, 1)]
    [InlineData(12, 1)]
    [InlineData(16, 1)]
    [InlineData(24, 39)]
    [InlineData(24, uint.MaxValue)]
    [InlineData(28, 511)]
    [InlineData(28, uint.MaxValue)]
    public void NvmeMalformedDescriptorCannotProduceValues(int offset, uint value)
    {
        var response = Response();
        Write(response, offset, value);
        Assert.Null(WindowsNvmeHealthReader.ParseHealthResponse(response));
        Assert.Null(WindowsNvmeHealthReader.ParseHealthResponse(Response()[..559]));
        Assert.Null(WindowsNvmeHealthReader.ParseHealthResponse(new byte[47]));
        Assert.Null(WindowsNvmeHealthReader.ParseHealthLog(new byte[511]));
    }

    [Fact]
    public void DeviceSerialIsBoundedAndRequiresActualNvmeBus()
    {
        var bytes = new byte[48];
        Write(bytes, 4, 48); Write(bytes, 24, 40); Write(bytes, 28, 17);
        "SN-A"u8.CopyTo(bytes.AsSpan(40));
        Assert.Equal("SN-A", WindowsNvmeHealthReader.ParseDeviceSerial(bytes));
        Write(bytes, 28, 7);
        Assert.Null(WindowsNvmeHealthReader.ParseDeviceSerial(bytes));
        Write(bytes, 28, 17); Write(bytes, 24, 48);
        Assert.Null(WindowsNvmeHealthReader.ParseDeviceSerial(bytes));
        Write(bytes, 24, 40); bytes.AsSpan(40).Fill(65);
        Assert.Null(WindowsNvmeHealthReader.ParseDeviceSerial(bytes));
        Write(bytes, 4, 4096);
        Assert.Null(WindowsNvmeHealthReader.ParseDeviceSerial(bytes));
    }

    [Fact]
    public void OsDiskNumberUsesUniqueIdentityNotPhysicalDeviceIdOrRowOrder()
    {
        var disk = Physical(); // DeviceId=7, OS number=23
        var os = OsDisk();
        Assert.Equal(23, WindowsStorageSensorsMonitoringZone.FindNvmeDiskNumber(disk, [os]));
        Assert.Null(WindowsStorageSensorsMonitoringZone.FindNvmeDiskNumber(disk, [os, OsDisk()]));
        os["SerialNumber"] = "other";
        Assert.Null(WindowsStorageSensorsMonitoringZone.FindNvmeDiskNumber(disk, [os]));
        os = OsDisk(); os["BusType"] = 7;
        Assert.Null(WindowsStorageSensorsMonitoringZone.FindNvmeDiskNumber(disk, [os]));
        disk["SerialNumber"] = null;
        Assert.Null(WindowsStorageSensorsMonitoringZone.FindNvmeDiskNumber(disk, [OsDisk()]));
    }

    [Fact]
    public void NvmeOnlyReadsSelectedExactDeviceAndMissingOrChangedIdentityIsEmpty()
    {
        var calls = new List<(int Number, string Serial)>();
        var zone = new WindowsStorageSensorsMonitoringZone((_, query) =>
            new(query.Contains("MSFT_PhysicalDisk") ? [Physical(), Physical(8, "DISK-B", "SN-B")] : [OsDisk(), OsDisk(24, "DISK-B", "SN-B")], null),
            (number, serial, unique, format) =>
            {
                Assert.Equal("DISK-A", unique); Assert.Equal(8, format);
                calls.Add((number, serial)); return WindowsNvmeHealthReader.ParseHealthResponse(Response());
            });
        var snapshot = zone.ReadNvmeSensorsCore(new(new Dictionary<int, string> { [7] = Identity }));
        Assert.Single(snapshot.Disks);
        Assert.Equal([(23, "SN-A")], calls);
        Assert.Empty(zone.ReadNvmeSensorsCore(new(new Dictionary<int, string> { [7] = "windows-storage|unique|STALE" })).Disks);
        Assert.Single(calls);
        Assert.Empty(zone.ReadNvmeSensorsCore(new(new Dictionary<int, string>())).Disks);
    }

    [Fact]
    public void ReliabilityPreservesLargeCountersAndRejectsAmbiguousCounterRows()
    {
        var counters = Counter();
        var zone = Zone([counters]);
        var disk = Assert.Single(zone.ReadStorageSensors().Disks);
        Assert.Equal(LargeCount, disk.IntegerValues[MetricFieldCode.DiskReadErrorsTotal]);
        Assert.Equal(150UL, disk.IntegerValues[MetricFieldCode.DiskWear]);
        Assert.Equal(90, disk.Values[MetricFieldCode.DiskTemperatureMaximum]);
        Assert.Equal(2UL, disk.IntegerValues[MetricFieldCode.DiskHealthStatus]);
        counters["WriteErrorsTotal"] = -1;
        Assert.False(WindowsStorageSensorsMonitoringZone.ReadReliabilityValues(counters).ContainsKey(MetricFieldCode.DiskWriteErrorsTotal));
        var ambiguous = Assert.Single(Zone([Counter(), Counter()]).ReadStorageSensors().Disks);
        Assert.False(ambiguous.IntegerValues.ContainsKey(MetricFieldCode.DiskWear));
        Assert.Null(ambiguous.TemperatureCelsius);
        // Device metadata remains independent of reliability-counter availability.
        Assert.Equal(2UL, ambiguous.IntegerValues[MetricFieldCode.DiskHealthStatus]);
    }

    [Fact]
    public void DefaultPlanPublishesAllStorageFieldsAndClearsFailureWithoutLosingDirectory()
    {
        var healthy = CommittedSnapshot();
        var values = Assert.Single(MetricDeviceFields.Read(healthy, Identity)).Fields;
        var diskPerformanceNames = new HashSet<string>(
        [
            "storage_all_read_bytes_per_second_actual",
            "storage_all_write_bytes_per_second_actual",
            "storage_all_read_iops_actual",
            "storage_all_write_iops_actual",
            "storage_all_active_percent_actual",
            "storage_all_queue_length_actual",
            "storage_all_latency_actual"
        ], StringComparer.Ordinal);
        var diskPerformanceFields = values.Where(value => diskPerformanceNames.Contains(value.Name)).ToArray();
        Assert.Equal(7, diskPerformanceFields.Length);
        Assert.All(diskPerformanceFields, value =>
        {
            Assert.Equal(SamplingObservationStatus.Unavailable, value.Status);
            Assert.Null(value.NumericValue);
            Assert.Null(value.IntegerValue);
            Assert.Null(value.BytesValue);
        });

        var existingHealthFields = values.Where(value => !diskPerformanceNames.Contains(value.Name)).ToArray();
        Assert.Equal(44, existingHealthFields.Length); // Preserve old temperature + 17 WMI + 26 NVMe.
        Assert.All(existingHealthFields.Where(value => value.Name != "storage_all_nvme_temperature_sensor_1_celsius"
            && !Enumerable.Range(2, 6).Any(i => value.Name == $"storage_all_nvme_temperature_sensor_{i}_celsius")),
            value => Assert.Equal(SamplingObservationStatus.Current, value.Status));
        Assert.Equal(MetricInteger.FromUnsigned128(LargeCount), values.Single(v => v.Name == "storage_all_read_errors_total_reported").IntegerValue);
        Assert.Equal(MetricInteger.Parse("255"), values.Single(v => v.Name == "storage_all_nvme_percentage_used_reported").IntegerValue);
        Assert.Null(values.Single(v => v.Name == "storage_all_nvme_critical_warning_reported").NumericValue);
        using var cache = new LastSuccessfulHardwareMetricSnapshot();
        var request = MetricDeviceFields.CreateRequest(healthy, Identity, values.Select(v => v.Name));
        cache.Publish(healthy, request);
        cache.RecordFailure(request, healthy.CapturedAt.AddSeconds(1), "fixture-unavailable");
        Assert.All(Assert.Single(MetricDeviceFields.Read(cache.Read()!, Identity)).Fields, value =>
        {
            Assert.Equal(SamplingObservationStatus.Unavailable, value.Status);
            Assert.Null(value.NumericValue); Assert.Null(value.IntegerValue); Assert.Null(value.BytesValue);
        });
        cache.Publish(healthy, request);
        var recovered = Assert.Single(MetricDeviceFields.Read(cache.Read()!, Identity)).Fields;
        Assert.Equal(values.Count, recovered.Count);
        Assert.All(recovered.Where(value => diskPerformanceNames.Contains(value.Name)), value =>
        {
            Assert.Equal(SamplingObservationStatus.Unavailable, value.Status);
            Assert.Null(value.NumericValue);
        });
        Assert.Equal(MetricInteger.Parse("255"), recovered.Single(v => v.Name == "storage_all_nvme_percentage_used_reported").IntegerValue);
    }

    [Fact]
    public void SataDeviceDoesNotDeclareNvmeAndReplacedDiskDoesNotPublishOldIdentity()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"storage-contract-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [new(7, Identity, 1)], [], workspace.CreateCatalogHandleMap());
        Assert.DoesNotContain(catalog.RuleBindings.Values, b => b.SourceId == NativeMetricSnapshotSourceCatalog.WindowsNvmeHealth);
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var modes = Modes(catalog);
        var now = DateTimeOffset.UtcNow;
        var plan = workspace.PlanCollection([catalog.MetricHandles["disk.7.wearPercent"]], modes, false, now);
        var source = Assert.Single(plan.Sources);
        var sensors = Zone([Counter()]).ReadStorageSensors();
        sensors = sensors with { Disks = [sensors.Disks[0] with { IdentityKey = "replacement" }] };
        var completion = WindowsHardwareMetricSampler.CreateStorageCompletion(plan, source, new(source.SourceHandle, 1, 1), now, sensors);
        workspace.CompleteSource(plan, completion, now);
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
        var value = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, Identity, new HashSet<string> { "storage_all_wear_reported" })).Fields);
        Assert.Equal(SamplingObservationStatus.Unavailable, value.Status);
        Assert.Null(value.IntegerValue);
    }

    [Fact]
    public void SuccessfulEnumerationDetectsRemovalReorderingAndCapabilityChangesButFailureDoesNotInventChange()
    {
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [], [new(7, Identity, 1) { SupportsNvmeHealth = true }], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var values = Zone([Counter()]).ReadStorageSensors();
        Assert.True(WindowsHardwareMetricSampler.MatchesStorageInventory(catalog, values));
        Assert.True(WindowsHardwareMetricSampler.MatchesStorageInventory(catalog, values with { Identities = null }));
        Assert.False(WindowsHardwareMetricSampler.MatchesStorageInventory(catalog, values with { Identities = [] }));
        Assert.False(WindowsHardwareMetricSampler.MatchesStorageInventory(catalog, values with
        { Identities = [new(8, Identity, 99) { SupportsNvmeHealth = true }] }));
        Assert.False(WindowsHardwareMetricSampler.MatchesStorageInventory(catalog, values with
        { Identities = [new(7, Identity, 99)] }));
        var rows = new[] { Physical(), Physical(7, "conflicting", "SN-B") };
        Assert.Empty(WindowsStorageSensorsMonitoringZone.ProjectIdentities(rows, 1));
        // Reject ambiguity in the original set; filtering one duplicate must not resurrect another.
        Assert.Empty(WindowsStorageSensorsMonitoringZone.ProjectIdentities([.. rows, Physical(8, "conflicting", "SN-B")], 1));
    }

    [Fact]
    public void CoreHostStorageIdentitiesCarryTheStorageDeviceDescriptor()
    {
        // The Core host reads storage identities through this zone; it must publish the same descriptor as the
        // ReadWriteCore default resources instead of the "unknown" placeholder.
        var physical = Physical();
        physical["FriendlyName"] = " YMTC PC41Q-1TB-B ";
        var read = WindowsStorageSensorsMonitoringZone.ReadIdentities(1, (_, _) => new([physical], null));
        var identity = Assert.Single(read.Identities);
        Assert.Equal("storage", identity.Device.Kind);
        var address = Assert.IsType<MetricDeviceAddress>(identity.Device.Address);
        Assert.Equal("storage", address.Kind);
        Assert.Equal(7, address.PhysicalDiskDeviceId);
        Assert.Equal("DISK-A", address.UniqueId);
        Assert.Equal("SN-A", address.SerialNumber);
        Assert.Equal("YMTC PC41Q-1TB-B", identity.Device.DisplayName);
        Assert.Equal("storage", Assert.Single(Zone([Counter()]).ReadStorageSensors().Identities!).Device.Kind);

        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [], read.Identities, [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var bindings = catalog.RuleBindings.Values.Where(row => row.DeviceIdentity == identity.ExactIdentityKey).ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, row =>
        {
            Assert.Equal("storage", row.Device.Kind);
            Assert.Equal("storage", row.Device.Address!.Kind);
            Assert.Equal("YMTC PC41Q-1TB-B", row.Device.DisplayName);
        });
    }

    internal static HardwareMetricSnapshot CommittedSnapshot()
        => StorageMetricSnapshotTestData.CommittedSnapshot();
}
