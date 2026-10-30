using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class AtaSmartInformationCollectorTests
{
    internal const string Device = "windows-storage|unique|ATA-A";
    internal const string Data = "storage_all_ata_smart_data_raw";
    internal const string Thresholds = "storage_all_ata_smart_thresholds_raw";

    [Fact]
    public void ReceiveRequestsContainOnlyReadCommandsAndNeverUseTheDisplayNumberAsDriveSelection()
    {
        foreach (var (page, feature) in new[] { (AtaSmartPages.Data, 0xD0), (AtaSmartPages.Thresholds, 0xD1) })
        {
            var input = WindowsAtaSmartReader.CreateReadQuery(page);
            Assert.Equal(32, input.Length);
            Assert.Equal(512U, BinaryPrimitives.ReadUInt32LittleEndian(input));
            Assert.Equal([(byte)feature, 1, 1, 0x4F, 0xC2, 0xA0, 0xB0, 0], input[4..12]);
            Assert.All(input[12..], value => Assert.Equal(0, value));
        }
        foreach (var pages in new[] { AtaSmartPages.None, AtaSmartPages.Data | AtaSmartPages.Thresholds, (AtaSmartPages)4 })
            Assert.Throws<ArgumentOutOfRangeException>(() => WindowsAtaSmartReader.CreateReadQuery(pages));
        Assert.Equal(0x0007C088U, WindowsAtaSmartReader.ReceiveIoctl);
        Assert.Equal(0xC0000000U, WindowsAtaSmartReader.DeviceAccess);
        Assert.Equal(new byte[12], WindowsStoragePropertyQuery.Create(0));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(WindowsStoragePropertyQuery.Create(2)));
    }

    [Fact]
    public void RawPagePreservesUnknownRevisionDuplicateIdsNormalizedAndVendorBytesWithoutInterpretation()
    {
        var page = Page(7);
        page[0] = 0xEF; page[1] = 0xBE; // Unknown revision preserved, not used as a vendor classifier.
        page[2] = page[14] = 250; // Duplicate/unknown attribute IDs remain exact raw bytes.
        page[5] = 255; page[6] = 254; // Normalized current/worst, not percentages.
        FixChecksum(page);
        var response = Response(page);
        Assert.Equal(page, WindowsAtaSmartReader.ParseReadResponse(response));
        var parsed = WindowsAtaSmartReader.ParseReadResponse(response)!;
        response[20] ^= 1;
        Assert.Equal(page, parsed); // Independent copy.
        Assert.Null(WindowsAtaSmartReader.ParseReadResponse(response));
        Assert.Null(WindowsAtaSmartReader.ParseReadResponse(Response(page)[..527]));
        Assert.Null(WindowsAtaSmartReader.ParseReadResponse([.. Response(page), 0]));
        var bad = Response(page); bad[4] = 1; bad[5] = 4;
        Assert.Null(WindowsAtaSmartReader.ParseReadResponse(bad));
        bad = Response(page); BinaryPrimitives.WriteUInt32LittleEndian(bad, 511);
        Assert.Null(WindowsAtaSmartReader.ParseReadResponse(bad));
        bad = Response(page); bad[5] = 255; // IDE error register is undefined on driver success.
        Assert.Equal(page, WindowsAtaSmartReader.ParseReadResponse(bad));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(11, true)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(10, false)]
    [InlineData(17, false)]
    [InlineData(0, false)]
    public void DeviceDescriptorRequiresAtaOrSataBusAndBoundsTheExactSerial(int bus, bool supported)
    {
        var bytes = Descriptor(bus);
        Assert.Equal(supported ? "SN-A" : null, WindowsAtaSmartReader.ParseDeviceSerial(bytes));
        if (!supported) return;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)bytes.Length);
        Assert.Null(WindowsAtaSmartReader.ParseDeviceSerial(bytes));
        bytes = Descriptor(bus); bytes[^1] = 65;
        Assert.Null(WindowsAtaSmartReader.ParseDeviceSerial(bytes));
        bytes = Descriptor(bus); bytes[36] = 255;
        Assert.Null(WindowsAtaSmartReader.ParseDeviceSerial(bytes));
        bytes = Descriptor(bus); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 4096);
        Assert.Null(WindowsAtaSmartReader.ParseDeviceSerial(bytes));
    }

    [Fact]
    public void OpenedDeviceReadVerifiesIdentitySelectsPagesAndKeepsPartialFailureIndependent()
    {
        var query = new FakeQuery { FailThresholds = true };
        var opened = new List<int>();
        var result = WindowsAtaSmartReader.Read(23, " sn-a ", AtaSmartPages.Data | AtaSmartPages.Thresholds,
            number => { opened.Add(number); return query; })!;
        Assert.Equal([23], opened);
        Assert.Equal([WindowsAtaSmartReader.QueryPropertyIoctl, WindowsAtaSmartReader.ReceiveIoctl, WindowsAtaSmartReader.ReceiveIoctl], query.Calls.Select(call => call.Ioctl));
        Assert.Equal([0xD0, 0xD1], query.Calls.Skip(1).Select(call => (int)call.Input[4]));
        Assert.Equal(Page(7), result.BytesValues[MetricFieldCode.AtaSmartDataRaw]);
        Assert.False(result.BytesValues.ContainsKey(MetricFieldCode.AtaSmartThresholdsRaw));
        Assert.True(query.Disposed);
        query = new();
        result = WindowsAtaSmartReader.Read(23, "SN-A", AtaSmartPages.Thresholds, _ => query)!;
        Assert.Equal(Page(13), Assert.Single(result.BytesValues).Value);
        Assert.Equal(0xD1, query.Calls[1].Input[4]);
        query = new();
        Assert.Null(WindowsAtaSmartReader.Read(23, "replacement", AtaSmartPages.Data, _ => query));
        Assert.Single(query.Calls);
        query = new() { Bus = 7 };
        Assert.Null(WindowsAtaSmartReader.Read(23, "SN-A", AtaSmartPages.Data, _ => query));
        Assert.Single(query.Calls);
        foreach (var pages in new[] { AtaSmartPages.None, (AtaSmartPages)4 })
            Assert.Null(WindowsAtaSmartReader.Read(23, "SN-A", pages, _ => throw new InvalidOperationException("must not open")));
        Assert.Null(WindowsAtaSmartReader.Read(-1, "SN-A", AtaSmartPages.Data, _ => throw new InvalidOperationException()));
        Assert.Null(WindowsAtaSmartReader.Read(23, "", AtaSmartPages.Data, _ => throw new InvalidOperationException()));
        Assert.Null(WindowsAtaSmartReader.Read(23, "SN-A", AtaSmartPages.Data, _ => null));
        query = new() { FailDescriptor = true };
        Assert.Null(WindowsAtaSmartReader.Read(23, "SN-A", AtaSmartPages.Data, _ => query));
        Assert.Single(query.Calls);
        query = new() { UniqueId = "replacement" };
        Assert.Null(WindowsAtaSmartReader.Read(23, "SN-A", AtaSmartPages.Data, _ => query, "ATA-A", 0));
        Assert.Equal(2, query.Calls.Count);
        Assert.All(query.Calls, call => Assert.Equal(WindowsAtaSmartReader.QueryPropertyIoctl, call.Ioctl));
        query = new();
        Assert.Null(WindowsAtaSmartReader.Read(23, "SN-A", AtaSmartPages.Data, _ => query, "ATA-A", null));
        Assert.Single(query.Calls);
        query = new();
        Assert.Equal(Page(7), WindowsAtaSmartReader.Read(23, "SN-A", AtaSmartPages.Data, _ => query, "ATA-A", 0)!
            .BytesValues[MetricFieldCode.AtaSmartDataRaw]);
        Assert.Equal(3, query.Calls.Count);
    }

    [Fact]
    public void UniqueIdRequiresExactTypedDeviceIdentifierAndRejectsUnknownEncodingOrMalformedChain()
    {
        var bytes = Identifiers("ATA-A");
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(bytes, "ATA-A", 0));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(bytes, "replacement", 0));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(bytes, "ATA-A", 1));
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(bytes[..^1], "ATA-A", 0));
        var port = Identifiers("ATA-A"); BinaryPrimitives.WriteUInt32LittleEndian(port.AsSpan(24), 1);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(port, "ATA-A", 0));
        var binary = Identifiers("ATA-A"); BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(12), 1);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(binary, "ATA-A", 0));
        var utf8 = Identifiers("设备标识", 3, 8);
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(utf8, "设备标识", 8));
        utf8[28] = 255;
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(utf8, "设备标识", 8));
        var bad = Identifiers("ATA-A"); BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(20), 65535);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(bad, "ATA-A", 0));
        bad = Identifiers("ATA-A"); BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(22), 1);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(bad, "ATA-A", 0));
        bad = Identifiers("ATA-A"); BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(8), 2);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(bad, "ATA-A", 0));
        var duplicate = new byte[54];
        Identifiers("ATA-A").CopyTo(duplicate, 0);
        Identifiers("ATA-A").AsSpan(12).CopyTo(duplicate.AsSpan(33));
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(4), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(duplicate.AsSpan(22), 21);
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(duplicate, "ATA-A", 0));
        duplicate[49] = (byte)'B';
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(duplicate, "ATA-A", 0));
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(37), 3); // Higher-priority NAA ID present: lower vendor alias is not selected identity.
        Assert.False(WindowsAtaSmartReader.MatchesUniqueId(duplicate, "ATA-A", 0));
    }

    [Fact]
    public void ZoneReadsOnlySelectedExactDeviceAndPageAndDoesNoIoWithoutDemand()
    {
        var calls = new List<(int, string, AtaSmartPages)>();
        var queries = 0;
        var zone = new WindowsStorageSensorsMonitoringZone((_, query) =>
        {
            queries++;
            return new(query.Contains("MSFT_PhysicalDisk") ? [Physical(), Physical(8, "ATA-B", "SN-B")]
                : [OsDisk(), OsDisk(24, "ATA-B", "SN-B")], null);
        }, (_, _, _, _) => throw new InvalidOperationException("NVMe must not read"), (number, serial, pages, unique, format) =>
        {
            Assert.Equal("ATA-A", unique); Assert.Equal(0, format);
            calls.Add((number, serial, pages));
            return new(new Dictionary<MetricFieldCode, byte[]> { [MetricFieldCode.AtaSmartDataRaw] = Page(7) });
        });
        var request = new AtaSmartSensorReadRequest(new Dictionary<int, AtaSmartDeviceReadRequest> { [7] = new(Device, AtaSmartPages.Data) });
        var snapshot = zone.ReadAtaSensors(request);
        Assert.Equal([(23, "SN-A", AtaSmartPages.Data)], calls);
        Assert.Equal(Device, Assert.Single(snapshot.Disks).IdentityKey);
        Assert.Equal(2, snapshot.Identities!.Count);
        Assert.Empty(zone.ReadAtaSensors(new(new Dictionary<int, AtaSmartDeviceReadRequest>())).Disks);
        Assert.Equal(2, queries);
        Assert.Empty(zone.ReadAtaSensors(new(new Dictionary<int, AtaSmartDeviceReadRequest> { [7] = new("stale", AtaSmartPages.Data) })).Disks);
        Assert.Single(calls);
    }

    [Fact]
    public void DiskAssociationRejectsReplacementsDuplicateIdentitiesAndNonAtaTransport()
    {
        var disk = Physical();
        Assert.Equal(23, WindowsStorageSensorsMonitoringZone.FindAtaDiskNumber(disk, [OsDisk()]));
        foreach (var key in new[] { "SerialNumber", "UniqueId", "Size", "Number", "BusType" })
        {
            var os = OsDisk();
            os[key] = key switch { "Size" => 1235UL, "Number" => -1, "BusType" => 7, _ => "replacement" };
            Assert.Null(WindowsStorageSensorsMonitoringZone.FindAtaDiskNumber(disk, [os]));
        }
        Assert.Null(WindowsStorageSensorsMonitoringZone.FindAtaDiskNumber(disk, [OsDisk(), OsDisk()]));
        Assert.Null(WindowsStorageSensorsMonitoringZone.FindAtaDiskNumber(disk, [OsDisk(), OsDisk(23, "other", "other")]));
        foreach (var bus in new[] { 7, 8, 10, 17, 0 })
        {
            disk = Physical(); disk["BusType"] = bus;
            Assert.Null(WindowsStorageSensorsMonitoringZone.FindAtaDiskNumber(disk, [OsDisk()]));
            Assert.False(Assert.Single(WindowsStorageSensorsMonitoringZone.ProjectIdentities([disk], 1)).SupportsAtaSmart);
        }
        Assert.Empty(WindowsStorageSensorsMonitoringZone.ProjectIdentities([Physical(), Physical(8)], 1));
        Assert.Empty(WindowsStorageSensorsMonitoringZone.ProjectIdentities([Physical(), Physical(7, "other", "other")], 1));
        Assert.True(Assert.Single(WindowsStorageSensorsMonitoringZone.ProjectIdentities([Physical()], 1)).SupportsAtaSmart);
    }

    [Fact]
    public void NativePlanCommitsExactPagesClearsFailureAndPersistsCatalogOnly()
    {
        using var fixture = new Fixture();
        var first = fixture.Commit(1, false);
        var fields = Assert.Single(MetricDeviceFields.Read(first, Device, new HashSet<string> { Data, Thresholds })).Fields;
        Assert.All(fields, field =>
        {
            Assert.Equal("bytes", field.ValueKind); Assert.Equal("windows.ata-smart", field.SourceId);
            Assert.Equal("B", field.Unit); Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue);
            Assert.Equal(SamplingObservationStatus.Current, field.Status);
        });
        Assert.Equal(Page(7), fields.Single(f => f.Name == Data).BytesValue);
        Assert.Equal(Page(13), fields.Single(f => f.Name == Thresholds).BytesValue);
        var request = MetricDeviceFields.CreateRequest(first, Device, [Data, Thresholds]);
        using var cache = new LastSuccessfulHardwareMetricSnapshot(); cache.Publish(first, request);
        var partial = fixture.Commit(2, true);
        cache.Publish(partial, request);
        var failed = Assert.Single(MetricDeviceFields.Read(cache.Read()!, Device, new HashSet<string> { Data, Thresholds })).Fields;
        Assert.Equal(Page(7), failed.Single(f => f.Name == Data).BytesValue);
        Assert.Equal(SamplingObservationStatus.Unavailable, failed.Single(f => f.Name == Thresholds).Status);
        Assert.Null(failed.Single(f => f.Name == Thresholds).BytesValue);
        Assert.Equal(0U, fixture.Workspace.ReadCommitted().Metrics.Single(m => m.MetricHandle == fixture.ThresholdHandle).ByteCount);
        cache.RecordFailure(request, first.CapturedAt.AddSeconds(1), "test-failure");
        Assert.All(Assert.Single(MetricDeviceFields.Read(cache.Read()!, Device, new HashSet<string> { Data, Thresholds })).Fields,
            field => Assert.Null(field.BytesValue));
        var recovered = fixture.Commit(3, false);
        fixture.Workspace.ExportAndPersist(fixture.Now.AddSeconds(1));
        using var restored = NativeMetricSnapshotHostConfiguration.CreateWorkspace(fixture.Compiled, fixture.File);
        var restoredCatalog = fixture.CreateCatalog(restored);
        Assert.Equal(fixture.DataHandle,
            restoredCatalog.MetricBindings.Values.Single(binding => binding.FieldCode == MetricFieldCode.AtaSmartDataRaw).MetricHandle);
        Assert.Equal(fixture.ThresholdHandle,
            restoredCatalog.MetricBindings.Values.Single(binding => binding.FieldCode == MetricFieldCode.AtaSmartThresholdsRaw).MetricHandle);
        restored.ReplaceCatalog(restoredCatalog, fixture.Now.AddSeconds(2));
        var persisted = NativeMetricSnapshotCommittedProjection.Create(frame: restored.ReadCommitted(), workspaceIdentity: 1);
        Assert.Null(Assert.Single(MetricDeviceFields.Read(persisted, Device, new HashSet<string> { Thresholds })).Fields.Single().BytesValue);
        Assert.Equal(Page(13), Assert.Single(MetricDeviceFields.Read(recovered, Device, new HashSet<string> { Thresholds })).Fields.Single().BytesValue);
        Assert.Equal(MonitoringSourceZoneIds.WindowsStorageSensors, NativeMetricSnapshotSourceCatalog.ResolveZoneId("windows.ata-smart"));
        Assert.DoesNotContain(fixture.Catalog.RuleBindings.Values, binding => binding.SourceId == "windows.nvme-health");
    }

    [Fact]
    public void SelectionUsesCompiledFieldCodesAndInventoryCapabilityChangesRequestDirectoryRefresh()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan(1, [fixture.DataHandle]);
        var source = Assert.Single(plan.Sources);
        var request = WindowsHardwareMetricSampler.CreateAtaSmartReadRequest(plan, source.SourceHandle);
        Assert.Equal(new(Device, AtaSmartPages.Data), Assert.Single(request.Devices).Value);
        var otherSourcePlan = fixture.Plan(2, [fixture.Catalog.MetricBindings.Values.Single(binding => binding.FieldCode == MetricFieldCode.DiskTemperature).MetricHandle]);
        Assert.Empty(WindowsHardwareMetricSampler.CreateAtaSmartReadRequest(otherSourcePlan, source.SourceHandle).Devices);
        var identity = new NativeMetricSnapshotOrdinalCatalogIdentity(7, Device, 1) { SupportsAtaSmart = true };
        Assert.True(WindowsHardwareMetricSampler.MatchesStorageInventory(fixture.Catalog,
            new(new("test", "Available", ""), []) { Identities = [identity] }));
        Assert.False(WindowsHardwareMetricSampler.MatchesStorageInventory(fixture.Catalog,
            new(new("test", "Available", ""), []) { Identities = [identity with { SupportsAtaSmart = false }] }));
        Assert.True(WindowsHardwareMetricSampler.MatchesStorageInventory(fixture.Catalog,
            new(new("test", "Unavailable", ""), []) { Identities = null }));
    }

    [Theory]
    [InlineData(511)]
    [InlineData(513)]
    public void NativeDefinitionRejectsWrongPageSizeWithoutChangingCommittedValues(int length)
    {
        using var fixture = new Fixture();
        fixture.Commit(1, false);
        var before = fixture.Workspace.ReadCommitted().Header.SemanticFingerprint;
        var plan = fixture.Plan(2, [fixture.DataHandle]);
        var source = Assert.Single(plan.Sources);
        var disk = new DiskTemperatureSensor(7, "test", null, "")
        { IdentityKey = Device, BytesValues = new Dictionary<MetricFieldCode, byte[]> { [MetricFieldCode.AtaSmartDataRaw] = new byte[length] } };
        var invalidRead = WindowsHardwareMetricSampler.CreateStorageCompletion(plan, source, new(source.SourceHandle, 1, 2),
            fixture.Now.AddMilliseconds(200), new(new("test", "Available", ""), [disk]));
        var empty = Assert.Single(invalidRead.Observations);
        Assert.Equal((uint)NativeMetricSnapshotObservationStatus.Unavailable, empty.Status);
        Assert.Equal(0U, empty.ByteCount);
        disk = disk with { BytesValues = new Dictionary<MetricFieldCode, byte[]> { [MetricFieldCode.AtaSmartDataRaw] = Page(7) } };
        var validRead = WindowsHardwareMetricSampler.CreateStorageCompletion(plan, source, new(source.SourceHandle, 1, 2),
            fixture.Now.AddMilliseconds(200), new(new("test", "Available", ""), [disk]));
        var observations = validRead.Observations.ToArray();
        observations[0].ByteCount = (uint)length;
        Assert.Throws<InvalidOperationException>(() => fixture.Workspace.CompleteSource(plan,
            validRead with { Observations = [.. observations] }, fixture.Now.AddMilliseconds(200)));
        Assert.Equal(before, fixture.Workspace.ReadCommitted().Header.SemanticFingerprint);
        fixture.Commit(3, false);
    }

    internal static HardwareMetricSnapshot CommittedSnapshot()
    {
        using var fixture = new Fixture();
        return fixture.Commit(1, false);
    }

    internal static byte[] Page(int seed)
    {
        var page = Enumerable.Range(0, 512).Select(i => (byte)(i * 31 + seed)).ToArray();
        FixChecksum(page); return page;
    }
    private static void FixChecksum(byte[] bytes)
    {
        bytes[511] = 0;
        bytes[511] = unchecked((byte)-bytes.Sum(value => value));
    }
    private static byte[] Response(byte[] page)
    {
        var bytes = new byte[528]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 512);
        page.CopyTo(bytes, 16); return bytes;
    }
    private static byte[] Descriptor(int bus)
    {
        var bytes = new byte[41]; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)bus);
        Encoding.ASCII.GetBytes("SN-A").CopyTo(bytes, 36); return bytes;
    }
    private static byte[] Identifiers(string value, uint codeSet = 2, uint type = 0)
    {
        var id = Encoding.UTF8.GetBytes(value);
        var bytes = new byte[28 + id.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), codeSet);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)id.Length);
        id.CopyTo(bytes, 28); return bytes;
    }
    private static Dictionary<string, object?> Physical(int id = 7, string unique = "ATA-A", string serial = "SN-A") => new()
    { ["DeviceId"] = id, ["UniqueId"] = unique, ["UniqueIdFormat"] = 0, ["SerialNumber"] = serial, ["Size"] = 1234UL, ["BusType"] = 11 };
    private static Dictionary<string, object?> OsDisk(int number = 23, string unique = "ATA-A", string serial = "SN-A") => new()
    { ["Number"] = number, ["UniqueId"] = unique, ["SerialNumber"] = serial, ["Size"] = 1234UL, ["BusType"] = 11 };

    private sealed class FakeQuery : IWindowsAtaSmartQuery
    {
        internal int Bus { get; init; } = 11;
        internal bool FailThresholds { get; init; }
        internal bool FailDescriptor { get; init; }
        internal string UniqueId { get; init; } = "ATA-A";
        internal bool Disposed { get; private set; }
        internal List<(uint Ioctl, byte[] Input)> Calls { get; } = [];
        public byte[]? Query(uint ioctl, byte[] input, int outputLength)
        {
            Calls.Add((ioctl, input.ToArray()));
            if (ioctl == WindowsAtaSmartReader.QueryPropertyIoctl)
            {
                Assert.Equal(12, input.Length); Assert.Equal(4096, outputLength);
                var property = BinaryPrimitives.ReadUInt32LittleEndian(input);
                Assert.Equal(WindowsStoragePropertyQuery.Create(property), input);
                Assert.Contains(property, new uint[] { 0, 2 });
                return property == 2 ? Identifiers(UniqueId) : FailDescriptor ? null : Descriptor(Bus);
            }
            Assert.Equal(WindowsAtaSmartReader.ReceiveIoctl, ioctl); // Never SMART_SEND_DRIVE_COMMAND.
            Assert.Equal(528, outputLength);
            Assert.Contains(input[4], new byte[] { 0xD0, 0xD1 });
            return FailThresholds && input[4] == 0xD1 ? null : Response(Page(input[4] == 0xD0 ? 7 : 13));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "ata-smart-" + Guid.NewGuid().ToString("N"));
        internal string File { get; }
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerMetricSnapshotPlan Compiled { get; }
            = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        internal NativeMetricSnapshotWorkspace Workspace { get; }
        internal NativeMetricSnapshotCatalogProjection Catalog { get; }
        internal ulong DataHandle => Catalog.MetricBindings.Values.Single(binding => binding.FieldCode == MetricFieldCode.AtaSmartDataRaw).MetricHandle;
        internal ulong ThresholdHandle => Catalog.MetricBindings.Values.Single(binding => binding.FieldCode == MetricFieldCode.AtaSmartThresholdsRaw).MetricHandle;
        internal Fixture()
        {
            Directory.CreateDirectory(directory); File = Path.Combine(directory, "cache.bin");
            Workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(Compiled, File); Catalog = CreateCatalog(Workspace); Workspace.ReplaceCatalog(Catalog, Now);
        }
        internal NativeMetricSnapshotCatalogProjection CreateCatalog(NativeMetricSnapshotWorkspace workspace)
            => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(Compiled, [], [new(7, Device, 1) { SupportsAtaSmart = true }], [], workspace.CreateCatalogHandleMap());
        internal NativeMetricSnapshotCollectionPlan Plan(ulong sequence, ulong[] selected)
            => Workspace.PlanCollection(selected, Catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle,
                1, NativeMetricSnapshotSourceAvailability.Available)).ToArray(), false, Now.AddMilliseconds((long)sequence * 100));
        internal HardwareMetricSnapshot Commit(ulong sequence, bool partialFailure)
        {
            var plan = Plan(sequence, [DataHandle, ThresholdHandle]);
            var source = Assert.Single(plan.Sources);
            Assert.Equal("windows.ata-smart", Catalog.SourceIds[source.SourceHandle]);
            var zone = new WindowsStorageSensorsMonitoringZone((_, query) => new(query.Contains("MSFT_PhysicalDisk") ? [Physical()] : [OsDisk()], null),
                (_, _, _, _) => throw new InvalidOperationException("wrong source"), (number, serial, pages, unique, format) =>
                    WindowsAtaSmartReader.Read(number, serial, pages, _ => new FakeQuery { FailThresholds = partialFailure }, unique, format));
            var sensors = zone.ReadAtaSensors(WindowsHardwareMetricSampler.CreateAtaSmartReadRequest(plan, source.SourceHandle));
            Workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateStorageCompletion(plan, source,
                new(source.SourceHandle, 1, sequence), Now.AddMilliseconds((long)sequence * 100), sensors), Now.AddMilliseconds((long)sequence * 100));
            return NativeMetricSnapshotCommittedProjection.Create(frame: Workspace.ReadCommitted(), workspaceIdentity: 1);
        }
        public void Dispose()
        {
            Workspace.Dispose(); foreach (var file in Directory.EnumerateFiles(directory)) System.IO.File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
