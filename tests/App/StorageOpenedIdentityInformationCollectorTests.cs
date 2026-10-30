using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class StorageOpenedIdentityInformationCollectorTests
{
    private const string HexIdentity = "000A10FF00414200";
    private static readonly byte[] IdentityBytes = Convert.FromHexString(HexIdentity);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void BinaryIdentityUsesEveryByteInOriginalOrderIncludingZeros(int type)
    {
        var descriptor = Identifier(IdentityBytes, (uint)type);
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(descriptor, HexIdentity, type));
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(descriptor, "  " + HexIdentity.ToLowerInvariant() + "  ", type));
        foreach (var other in new[] { "A10FF041420", HexIdentity[..^2], "eui." + HexIdentity,
                     "naa." + HexIdentity, "0x" + HexIdentity, "00:0A:10:FF:00:41:42:00", "00424100FF100A00", "000A10FF00414201" })
            Assert.False(WindowsAtaSmartReader.MatchesUniqueId(descriptor, other, type));
    }

    [Fact]
    public void BinaryIdentityStillRequiresSelectedTypeDeviceAssociationValidChainAndOneMatch()
    {
        var descriptor = Identifier(IdentityBytes);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(descriptor, HexIdentity, 3));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(Identifier(IdentityBytes, association: 1), HexIdentity, 2));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(Identifier(IdentityBytes, 8), HexIdentity, 8));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(descriptor[..^1], HexIdentity, 2));
        var duplicate = Chain(descriptor, descriptor);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(duplicate, HexIdentity, 2));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(Chain(descriptor, Identifier([0x51, 1, 2, 3, 4, 5, 6, 7], 3)), HexIdentity, 2));
        var invalid = descriptor.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(22), 1);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(invalid, HexIdentity, 2));
    }

    [Theory]
    [InlineData(StorageIdentifyProtocol.Ata)] [InlineData(StorageIdentifyProtocol.NvmeController)]
    public void IdentifyUsesBinaryUniqueIdOnTheSameOpenedObjectBeforeReadingProtocol(StorageIdentifyProtocol protocol)
    {
        var serial = protocol == StorageIdentifyProtocol.Ata ? "SERIAL-A" : "SERIAL-N";
        var raw = protocol == StorageIdentifyProtocol.Ata ? StorageIdentifyInformationCollectorTests.AtaRaw()
            : StorageIdentifyInformationCollectorTests.NvmeRaw();
        var response = WindowsStorageIdentifyReader.CreateIdentifyQuery(protocol);
        Write(response, 0, 48); Write(response, 4, 48); raw.CopyTo(response, 48);
        var device = new Device(protocol == StorageIdentifyProtocol.Ata ? 11 : 17, serial, response);
        Assert.NotNull(WindowsStorageIdentifyReader.Read(23, protocol, serial, HexIdentity, 2, _ => device));
        Assert.Equal(new uint[] { 0, 2, protocol == StorageIdentifyProtocol.Ata ? 50U : 49U }, device.Properties);
        Assert.True(device.Disposed);
        device = new(protocol == StorageIdentifyProtocol.Ata ? 11 : 17, serial, response) { Id = Identifier([0xFF, 0xEE], 2) };
        Assert.Null(WindowsStorageIdentifyReader.Read(23, protocol, serial, HexIdentity, 2, _ => device));
        Assert.Equal(new uint[] { 0, 2 }, device.Properties); Assert.True(device.Disposed);
    }

    [Fact]
    public void AtaSmartUsesBinaryIdentityBeforeReadingSelectedPage()
    {
        var response = new byte[528]; Write(response, 0, 512);
        AtaSmartInformationCollectorTests.Page(7).CopyTo(response, 16);
        var device = new Device(11, "SERIAL-A", response);
        var result = WindowsAtaSmartReader.Read(23, "SERIAL-A", AtaSmartPages.Data, _ => device, HexIdentity, 2)!;
        Assert.Equal(AtaSmartInformationCollectorTests.Page(7), Assert.Single(result.BytesValues).Value);
        Assert.Equal(new uint[] { 0, 2, uint.MaxValue }, device.Properties); Assert.True(device.Disposed);
        device = new(11, "SERIAL-A", response) { Id = Identifier([0xFF, 0xEE], 2) };
        Assert.Null(WindowsAtaSmartReader.Read(23, "SERIAL-A", AtaSmartPages.Data, _ => device, HexIdentity, 2));
        Assert.Equal(new uint[] { 0, 2 }, device.Properties); Assert.True(device.Disposed);
    }

    [Fact]
    public void NvmeHealthRequiresSameOpenedSerialBusAndUniqueIdAndOnlyFixedReadQueries()
    {
        var device = new Device(17, "SERIAL-N", HealthResponse());
        var opens = 0;
        var result = WindowsNvmeHealthReader.Read(23, "SERIAL-N", HexIdentity, 2, number =>
            { Assert.Equal(23, number); opens++; return device; })!;
        Assert.Equal(1, opens); Assert.Equal(0xA5UL, result.IntegerValues[MetricFieldCode.NvmeCriticalWarning]);
        Assert.Equal(new uint[] { 0, 2, 49 }, device.Properties); Assert.True(device.Disposed);
        foreach (var (bus, serial, unique, format, ids, expectedCalls) in new[]
        {
            (7, "SERIAL-N", HexIdentity, (int?)2, (byte[]?)Identifier(IdentityBytes), 1),
            (17, "other", HexIdentity, (int?)2, (byte[]?)Identifier(IdentityBytes), 1),
            (17, "SERIAL-N", HexIdentity, (int?)null, (byte[]?)Identifier(IdentityBytes), 1),
            (17, "SERIAL-N", HexIdentity, (int?)9, (byte[]?)Identifier(IdentityBytes), 1),
            (17, "SERIAL-N", "other", (int?)2, (byte[]?)Identifier(IdentityBytes), 2),
            (17, "SERIAL-N", HexIdentity, (int?)2, (byte[]?)null, 2)
        })
        {
            device = new(bus, serial, HealthResponse()) { Id = ids };
            Assert.Null(WindowsNvmeHealthReader.Read(23, "SERIAL-N", unique, format, _ => device));
            Assert.Equal(expectedCalls, device.Properties.Count); Assert.True(device.Disposed);
        }
        Assert.Null(WindowsNvmeHealthReader.Read(-1, "SERIAL-N", HexIdentity, 2, _ => throw new InvalidOperationException()));
        Assert.Null(WindowsNvmeHealthReader.Read(23, " ", HexIdentity, 2, _ => throw new InvalidOperationException()));
        Assert.Null(WindowsNvmeHealthReader.Read(23, "SERIAL-N", HexIdentity, 2, _ => null));
        device = new(17, "SERIAL-N", HealthResponse());
        Assert.NotNull(WindowsNvmeHealthReader.Read(23, "SERIAL-N", null, null, _ => device));
        Assert.Equal(new uint[] { 0, 49 }, device.Properties); Assert.True(device.Disposed);
    }

    [Fact]
    public void OriginalNvmeZonePassesSelectedIdentityAndFailurePublishesNoOldValues()
    {
        var identity = "windows-storage|unique|" + HexIdentity;
        var physical = new Dictionary<string, object?> { ["DeviceId"] = 7, ["UniqueId"] = HexIdentity,
            ["UniqueIdFormat"] = 2, ["SerialNumber"] = "SERIAL-N", ["BusType"] = 17, ["Size"] = 1234UL };
        var os = new Dictionary<string, object?>(physical) { ["Number"] = 23 };
        var queryCount = 0; var opens = 0; var changed = false;
        var zone = new WindowsStorageSensorsMonitoringZone((_, sql) =>
        {
            queryCount++;
            if (sql.Contains("MSFT_PhysicalDisk")) Assert.Contains("UniqueIdFormat", sql);
            return new(sql.Contains("MSFT_PhysicalDisk") ? [physical] : [os], null);
        }, (number, serial, unique, format) => WindowsNvmeHealthReader.Read(number, serial, unique, format, target =>
        {
            Assert.Equal(23, target); opens++;
            return new Device(17, "SERIAL-N", HealthResponse())
                { Id = changed ? Identifier([0xFF, 0xEE], 2) : Identifier(IdentityBytes) };
        }));
        var request = new StorageSensorReadRequest(new Dictionary<int, string> { [7] = identity });
        var current = Assert.Single(zone.ReadNvmeSensorsCore(request).Disks);
        Assert.Equal(identity, current.IdentityKey); Assert.Equal(0xA5UL, current.IntegerValues[MetricFieldCode.NvmeCriticalWarning]);
        Assert.Equal(1, opens); changed = true;
        var empty = zone.ReadNvmeSensorsCore(request);
        Assert.Empty(empty.Disks); Assert.Equal(identity, Assert.Single(empty.Identities!).ExactIdentityKey);
        var previousQueries = queryCount;
        Assert.Empty(zone.ReadNvmeSensorsCore(new(new Dictionary<int, string>())).Disks);
        Assert.Equal(previousQueries, queryCount); Assert.Equal(2, opens);

        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"storage-opened-identity-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [],
            [new(7, identity, 1) { SupportsNvmeHealth = true }], [], workspace.CreateCatalogHandleMap());
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var selected = catalog.MetricBindings.Values.Where(binding => binding.DeviceIdentity == identity
            && binding.FieldCode is MetricFieldCode.NvmeCriticalWarning or MetricFieldCode.NvmeHealthLogRaw)
            .Select(binding => binding.MetricHandle).ToArray();
        Assert.Equal(2, selected.Length);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle, 1,
            NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        for (var capture = 1; capture <= 3; capture++)
        {
            changed = capture == 2;
            var observedAt = now.AddSeconds(capture);
            var plan = workspace.PlanCollection(selected, modes, false, observedAt);
            var source = Assert.Single(plan.Sources);
            Assert.Equal(NativeMetricSnapshotSourceCatalog.WindowsNvmeHealth, catalog.SourceIds[source.SourceHandle]);
            var values = zone.ReadNvmeSensorsCore(request);
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateStorageCompletion(plan, source,
                new(source.SourceHandle, 1, (ulong)capture), observedAt, values), observedAt);
            var committed = NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
            var fields = Assert.Single(MetricDeviceFields.Read(committed, identity, new HashSet<string>
                { "storage_all_nvme_critical_warning_reported", "storage_all_nvme_health_log_raw" })).Fields;
            Assert.Equal(2, fields.Count);
            if (changed) Assert.All(fields, field =>
            {
                Assert.Equal(SamplingObservationStatus.Unavailable, field.Status);
                Assert.Null(field.IntegerValue); Assert.Null(field.BytesValue); Assert.Null(field.NumericValue);
            });
            else
            {
                Assert.Equal(MetricInteger.Parse("165"), fields.Single(field => field.Name == "storage_all_nvme_critical_warning_reported").IntegerValue);
                Assert.Equal(HealthResponse().AsSpan(48).ToArray(), fields.Single(field => field.Name == "storage_all_nvme_health_log_raw").BytesValue);
                Assert.All(fields, field => Assert.Equal(SamplingObservationStatus.Current, field.Status));
            }
        }
    }

    private static byte[] Identifier(byte[] value, uint type = 2, uint association = 0)
    {
        var bytes = new byte[28 + value.Length]; Write(bytes, 0, 16); Write(bytes, 4, (uint)bytes.Length);
        Write(bytes, 8, 1); Write(bytes, 12, 1); Write(bytes, 16, type); Write(bytes, 24, association);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)value.Length); value.CopyTo(bytes, 28);
        return bytes;
    }
    private static byte[] Chain(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length - 12]; first.CopyTo(result, 0);
        second.AsSpan(12).CopyTo(result.AsSpan(first.Length)); Write(result, 4, (uint)result.Length); Write(result, 8, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22), (ushort)(first.Length - 12)); return result;
    }
    private static byte[] HealthResponse()
    {
        var response = WindowsNvmeHealthReader.CreateHealthQuery(); Write(response, 0, 48); Write(response, 4, 48);
        response[48] = 0xA5; return response;
    }
    private static void Write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private sealed class Device(int bus, string serial, byte[] response) : IWindowsStorageIdentifyQuery, IWindowsNvmeHealthQuery, IWindowsAtaSmartQuery
    {
        internal byte[]? Id { get; init; } = Identifier(IdentityBytes);
        internal List<uint> Properties { get; } = [];
        internal bool Disposed { get; private set; }
        public byte[]? Query(byte[] input, int outputLength)
        {
            var property = BinaryPrimitives.ReadUInt32LittleEndian(input); Properties.Add(property);
            if (property is 0 or 2)
            {
                Assert.Equal(WindowsStoragePropertyQuery.Create(property), input); Assert.Equal(4096, outputLength);
                if (property == 2) return Id;
                var descriptor = new byte[37 + serial.Length]; Write(descriptor, 0, 36); Write(descriptor, 4, (uint)descriptor.Length);
                Write(descriptor, 24, 36); Write(descriptor, 28, (uint)bus); Encoding.ASCII.GetBytes(serial).CopyTo(descriptor, 36);
                return descriptor;
            }
            Assert.Contains(property, new uint[] { 49, 50 }); Assert.Equal(response.Length, outputLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(12)) == 2)
                Assert.Equal(WindowsNvmeHealthReader.CreateHealthQuery(), input);
            return response;
        }
        public byte[]? Query(uint ioctl, byte[] input, int outputLength)
        {
            if (ioctl == WindowsAtaSmartReader.QueryPropertyIoctl) return Query(input, outputLength);
            Assert.Equal(WindowsAtaSmartReader.ReceiveIoctl, ioctl); Assert.Equal(528, outputLength);
            Assert.Equal(WindowsAtaSmartReader.CreateReadQuery(AtaSmartPages.Data), input);
            Properties.Add(uint.MaxValue); return response;
        }
        public void Dispose() => Disposed = true;
    }
}
