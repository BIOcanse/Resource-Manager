using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite;
using SdkQuery = ResourceManager.ReadWrite.Windows.Platform.QueryResult;

namespace Resource_Manager_APP.Tests;

public sealed class StorageOwnerLibraryIntegrationTests
{
    [Fact]
    public void CompleteCapabilityCaptureHasOneCacheAndFailedRefreshDoesNotAdvanceIt()
    {
        var clock = new Clock();
        var queries = new List<string>();
        var fail = false;
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), clock, _ => BackendMessage.Create(0, 0),
            capabilityQuery: (_, command) =>
            {
                queries.Add(command);
                return new([], fail && command.EndsWith("FROM Win32_DiskPartition", StringComparison.Ordinal) ? "partition failed" : null);
            });
        var first = owner.ReadSnapshot();
        Assert.Null(first.Error); Assert.Equal(6, queries.Count);
        clock.Now += TimeSpan.FromSeconds(14);
        Assert.Same(first, owner.ReadSnapshot()); Assert.Equal(6, queries.Count);
        clock.Now += TimeSpan.FromSeconds(1); fail = true;
        Assert.Equal("partition failed", owner.ReadSnapshot().Error); Assert.Equal(12, queries.Count);
        Assert.Equal("partition failed", owner.ReadSnapshot().Error); Assert.Equal(18, queries.Count);
        fail = false;
        var refreshed = owner.ReadSnapshot(); Assert.NotSame(first, refreshed); Assert.Equal(24, queries.Count);
        Assert.Same(refreshed, owner.ReadSnapshot()); Assert.Equal(24, queries.Count);
    }

    [Fact]
    public void OptionalMsftFailureIsSuccessfulAndVolumeUsesAccountAvailableFreeSpaceInSameCapture()
    {
        var order = new List<string>(); var volumeReads = 0;
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), new Clock(), hasMsft => BackendMessage.Create(0, hasMsft ? (byte)1 : (byte)2),
            capabilityQuery: (_, command) =>
            {
                order.Add(command);
                if (command.EndsWith("FROM Win32_DiskDrive", StringComparison.Ordinal))
                    return new([Row(("Index", 7), ("DeviceID", "DISK"), ("PNPDeviceID", "SCSI\\DISK"))], null);
                if (command.EndsWith("FROM Win32_DiskPartition", StringComparison.Ordinal))
                    return new([Row(("DeviceID", "PART"), ("DiskIndex", 7), ("Index", 0))], null);
                if (command.EndsWith("FROM Win32_LogicalDisk", StringComparison.Ordinal))
                    return new([Row(("DeviceID", "Q:"), ("Size", 99UL), ("FreeSpace", 90UL))], null);
                if (command.EndsWith("FROM Win32_LogicalDiskToPartition", StringComparison.Ordinal))
                    return new([Row(("Antecedent", "DeviceID=\"PART\""), ("Dependent", "DeviceID=\"Q:\""))], null);
                return new([], command.EndsWith("FROM MSFT_Disk", StringComparison.Ordinal) ? "optional unavailable" : null);
            }, volumeQuery: letter => { Assert.Equal(6, order.Count); Assert.Equal("Q:", letter); volumeReads++; return new(true, "RecordedFS", 100, 17); });
        var snapshot = owner.ReadSnapshot();
        Assert.Null(snapshot.Error); Assert.Equal(6, order.Count); Assert.Equal(1, volumeReads);
        var device = Assert.Single(snapshot.Devices).Device;
        Assert.Equal((byte)2, device.Source.Code);
        var volume = Assert.Single(Assert.Single(device.Partitions).Volumes);
        Assert.Equal(17UL, volume.FreeBytes); Assert.Equal(100UL, volume.CapacityBytes); Assert.Equal("RecordedFS", volume.FileSystem);
        Assert.Same(snapshot, owner.ReadSnapshot()); Assert.Equal(1, volumeReads);
    }

    [Fact]
    public void InformationUsesOriginalExactMatcherThenPublicCanonicalBackendAndClearsFailedPayload()
    {
        var reads = new List<int>(); var failed = false;
        var owner = RecordedOwner((_, _, _, _, _) => { reads.Add(23); return failed ? null : new(null, WindowsStorageIdentifyReader.ParseNvmeController(IdentifyPayload())); });
        var declarations = owner.ReadInformationDeclarations()!;
        Assert.Equal(new[] { 23 }, reads);
        var identity = Assert.Single(declarations).DeviceIdentity;
        var current = WindowsInformationFieldProjection.Storage(declarations, DateTimeOffset.UtcNow);
        var backend = new InformationReadWriteBackend(() => current, _ => ValueTask.FromResult(current), _ => new OperationSupport(true, null, []));
        var field = new FieldReference(identity, "storage_all_nvme_controller_accessible_total_nvm_capacity_reported_bytes");
        Assert.NotNull(backend.ReadCached(field).Value);
        failed = true;
        current = WindowsInformationFieldProjection.Storage(owner.ReadInformationDeclarations()!, DateTimeOffset.UtcNow);
        Assert.Null(backend.ReadCached(field).Value); Assert.Equal(2, reads.Count);
        Assert.Equal(typeof(WindowsStorageIdentifyReader).Assembly, typeof(WindowsStorageOwner).Assembly);
        Assert.Equal(typeof(WindowsStorageOwner).Assembly, typeof(DeviceTopologyStorageSnapshot).Assembly);
    }

    [Fact]
    public void DuplicateOsNumberNeverInvokesNativeIdentifyAndFailedEnumerationHasNullDirectory()
    {
        var nativeCalls = 0;
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), new Clock(), _ => BackendMessage.Create(0, 0),
            query: (_, command) => command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal)
                ? new([Physical()], null) : new([OsDisk(), Row(("Number", 23), ("SerialNumber", "other"))], null),
            readIdentify: (_, _, _, _, _) => { nativeCalls++; return null; });
        Assert.Null(Assert.Single(owner.ReadInformationDeclarations()!).Nvme);
        Assert.Equal(0, nativeCalls);
        var failed = new WindowsStorageOwner(TimeSpan.FromSeconds(15), new Clock(), _ => BackendMessage.Create(0, 0), query: (_, _) => new([], "failed"));
        Assert.Null(failed.ReadInformationDeclarations());
        Assert.Null(failed.ReadNvmeSensorsCore(new(new Dictionary<int, string> { [7] = "stale" })).Identities);
    }

    [Fact]
    public void SelectedAtaPagesAndIdentityEvidenceReachTheOriginalNativeDelegate()
    {
        var calls = 0; var queries = 0;
        var physical = Physical(); physical["BusType"] = 11;
        var os = OsDisk(); os["BusType"] = 11;
        var identity = WindowsStorageDeviceIdentity.Create("unique", "serial");
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), new Clock(), _ => BackendMessage.Create(0, 0),
            query: (_, command) => { queries++; return new([command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? physical : os], null); },
            readAta: (number, serial, pages, unique, format) =>
            {
                Assert.Equal(23, number); Assert.Equal("serial", serial); Assert.Equal(AtaSmartPages.Thresholds, pages);
                Assert.Equal("unique", unique); Assert.Equal(1, format); calls++;
                return new(new Dictionary<ResourceManager.App.Domain.Metrics.MetricFieldCode, byte[]> { [ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartThresholdsRaw] = new byte[512] });
            });
        Assert.Empty(owner.ReadAtaSensorsCore(new(new Dictionary<int, AtaSmartDeviceReadRequest>())).Disks); Assert.Equal(0, queries);
        Assert.Empty(owner.ReadAtaSensorsCore(new(new Dictionary<int, AtaSmartDeviceReadRequest> { [7] = new("stale", AtaSmartPages.Data) })).Disks);
        Assert.Equal(0, calls);
        Assert.Single(owner.ReadAtaSensorsCore(new(new Dictionary<int, AtaSmartDeviceReadRequest> { [7] = new(identity, AtaSmartPages.Thresholds) })).Disks);
        Assert.Equal(1, calls);
    }

    private static WindowsStorageOwner RecordedOwner(Func<int, StorageIdentifyProtocol, string, string?, int?, StorageIdentifyRead?> read)
        => new(TimeSpan.FromSeconds(15), new Clock(), _ => BackendMessage.Create(0, 0),
            query: (_, command) => new SdkQuery([command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? Physical() : OsDisk()], null), readIdentify: read);
    private static Dictionary<string, object?> Physical() => Row(("DeviceId", 7), ("SerialNumber", "serial"), ("UniqueId", "unique"), ("UniqueIdFormat", 1), ("Size", 100UL), ("BusType", 17));
    private static Dictionary<string, object?> OsDisk() => Row(("Number", 23), ("SerialNumber", "serial"), ("UniqueId", "unique"), ("Size", 100UL), ("BusType", 17));
    private static byte[] IdentifyPayload() { var bytes = new byte[4096]; bytes[0] = 1; bytes[80] = 0; bytes[81] = 2; bytes[82] = 1; bytes[256] = 8; bytes[280] = 1; bytes[516] = 1; return bytes; }
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) => values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
