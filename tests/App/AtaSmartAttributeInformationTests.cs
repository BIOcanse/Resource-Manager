using System.Buffers.Binary;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Platform;
using QueryResult = ResourceManager.ReadWrite.Windows.Platform.QueryResult;
using Xunit;

public sealed class AtaSmartAttributeInformationTests
{
    [Fact]
    public void ParserPreservesOrderedSlotsFlagsRawWidthRevisionAndThresholdBytes()
    {
        var data = DataPage();
        var threshold = ThresholdPage();
        var parsedData = WindowsAtaSmartReader.ParseAttributeDataPage(data)!;
        var parsedThreshold = WindowsAtaSmartReader.ParseAttributeThresholdPage(threshold)!;

        Assert.Equal(0x1234, parsedData.Revision);
        Assert.Equal(30, parsedData.Slots.Count);
        Assert.Equal((byte)0, parsedData.Slots[2].AttributeId); // A zero-ID slot is kept in-place.
        Assert.Equal(0xA531, parsedData.Slots[0].Flags);
        Assert.Equal((byte)0x64, parsedData.Slots[0].CurrentNormalized);
        Assert.Equal((byte)0x37, parsedData.Slots[0].WorstNormalized);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 }, parsedData.Slots[0].RawSixBytes);
        Assert.Equal((byte)0x7E, parsedData.Slots[0].ReservedByte);
        Assert.Equal(0x1234, parsedThreshold.Revision);
        Assert.Equal((byte)9, parsedThreshold.Slots[0].AttributeId);
        Assert.Equal((byte)0x40, parsedThreshold.Slots[0].Threshold);
        Assert.Equal(Enumerable.Range(1, 10).Select(static value => (byte)value), parsedThreshold.Slots[0].ReservedTenBytes);

        data[200] ^= 1;
        threshold[511] ^= 1;
        Assert.Null(WindowsAtaSmartReader.ParseAttributeDataPage(data));
        Assert.Null(WindowsAtaSmartReader.ParseAttributeThresholdPage(threshold));
        Assert.Null(WindowsAtaSmartReader.ParseAttributeDataPage(new byte[511]));
        Assert.Null(WindowsAtaSmartReader.ParseAttributeThresholdPage(new byte[513]));
    }

    [Fact]
    public void OwnerReadsOnlySelectedPagesAndProjectsIndependentStructuredTables()
    {
        var physical = Physical();
        var identity = WindowsStorageOwner.ProjectIdentities([physical]).Single().ExactIdentityKey;
        var calls = new List<(int Number, string Serial, AtaSmartPages Pages, string? UniqueId, int? Format)>();
        var queries = 0;
        var owner = Owner((_, command) =>
        {
            queries++;
            return new QueryResult([command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? physical : OsDisk()], null);
        }, (number, serial, pages, unique, format) =>
        {
            calls.Add((number, serial, pages, unique, format));
            var values = new Dictionary<ResourceManager.App.Domain.Metrics.MetricFieldCode, byte[]>();
            if ((pages & AtaSmartPages.Data) != 0) values[ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartDataRaw] = DataPage();
            if ((pages & AtaSmartPages.Thresholds) != 0) values[ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartThresholdsRaw] = ThresholdPage();
            return new(values);
        });

        var selected = AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Threshold;
        var snapshot = owner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal) { [identity] = selected }));

        Assert.Equal(2, queries);
        Assert.Equal([(23, "serial", AtaSmartPages.Data | AtaSmartPages.Thresholds, "unique", (int?)1)], calls);
        Assert.NotNull(snapshot.ObservedAt);
        var normalized = snapshot.FindField(new(identity, "storage_all_ata_smart_attribute_normalized"))!;
        var raw = snapshot.FindField(new(identity, "storage_all_ata_smart_attribute_raw"))!;
        var threshold = snapshot.FindField(new(identity, "storage_all_ata_smart_attribute_threshold"))!;
        Assert.Equal("object", normalized.Definition.ValueKind);
        Assert.Equal(30, normalized.Value!.Value.GetProperty("slots").GetArrayLength());
        Assert.Equal(0x1234, normalized.Value.Value.GetProperty("revision").GetUInt16());
        Assert.Equal((ushort)0xA531, normalized.Value.Value.GetProperty("slots")[0].GetProperty("flags").GetUInt16());
        Assert.Null(raw.Value); // The unselected raw view does not leak from the shared data page.
        Assert.Equal(30, threshold.Value!.Value.GetProperty("slots").GetArrayLength());
        Assert.Equal((byte)0x40, threshold.Value.Value.GetProperty("slots")[0].GetProperty("threshold").GetByte());
        Assert.Equal((byte)4, threshold.Value.Value.GetProperty("slots")[1].GetProperty("attributeId").GetByte());
        Assert.Equal((byte)9, threshold.Value.Value.GetProperty("slots")[2].GetProperty("attributeId").GetByte());
        Assert.Equal(FieldObservationStatus.Current,
            InformationFieldProjection.Read(identity, normalized, snapshot.ObservedAt).Status);

        var rawOnly = owner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { [identity] = AtaSmartAttributeInformationFields.Raw }));
        Assert.Equal(AtaSmartPages.Data, calls[^1].Pages);
        var rawValue = rawOnly.FindField(new(identity, "storage_all_ata_smart_attribute_raw"))!.Value!.Value;
        Assert.Equal(Convert.ToBase64String(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 }),
            rawValue.GetProperty("slots")[0].GetProperty("rawSixBytes").GetString());
        Assert.Null(rawOnly.FindField(new(identity, "storage_all_ata_smart_attribute_normalized"))!.Value);
    }

    [Fact]
    public void PageFailureDuplicateIdentityAndNoSelectionRemainUnavailableWithoutReading()
    {
        var physical = Physical();
        var identity = WindowsStorageOwner.ProjectIdentities([physical]).Single().ExactIdentityKey;
        var reads = 0;
        var queries = 0;
        var owner = Owner((_, command) =>
        {
            queries++;
            return new QueryResult([command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? physical : OsDisk()], null);
        }, (_, _, pages, _, _) =>
        {
            reads++;
            var values = new Dictionary<ResourceManager.App.Domain.Metrics.MetricFieldCode, byte[]>();
            if ((pages & AtaSmartPages.Data) != 0) values[ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartDataRaw] = DataPage();
            // A selected threshold transfer can fail independently of the already-read data page.
            return new(values);
        });

        var failedThreshold = owner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { [identity] = AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw
                | AtaSmartAttributeInformationFields.Threshold }));
        Assert.NotNull(failedThreshold.FindField(new(identity, "storage_all_ata_smart_attribute_normalized"))!.Value);
        Assert.NotNull(failedThreshold.FindField(new(identity, "storage_all_ata_smart_attribute_raw"))!.Value);
        Assert.Null(failedThreshold.FindField(new(identity, "storage_all_ata_smart_attribute_threshold"))!.Value);
        Assert.Equal(1, reads);

        var failedDataOwner = Owner((_, command) => new QueryResult(
                [command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? physical : OsDisk()], null),
            (_, _, pages, _, _) =>
            {
                var values = new Dictionary<ResourceManager.App.Domain.Metrics.MetricFieldCode, byte[]>();
                if ((pages & AtaSmartPages.Data) != 0)
                {
                    var malformedData = DataPage();
                    malformedData[200] ^= 1;
                    values[ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartDataRaw] = malformedData;
                }
                if ((pages & AtaSmartPages.Thresholds) != 0)
                    values[ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartThresholdsRaw] = ThresholdPage();
                return new(values);
            });
        var failedData = failedDataOwner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { [identity] = AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw
                | AtaSmartAttributeInformationFields.Threshold }));
        Assert.Null(failedData.FindField(new(identity, "storage_all_ata_smart_attribute_normalized"))!.Value);
        Assert.Null(failedData.FindField(new(identity, "storage_all_ata_smart_attribute_raw"))!.Value);
        Assert.NotNull(failedData.FindField(new(identity, "storage_all_ata_smart_attribute_threshold"))!.Value);

        var noSelection = owner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { [identity] = AtaSmartAttributeInformationFields.None }));
        Assert.Equal(2, queries); // No additional WMI or ATA read for a passive field catalog projection.
        Assert.Equal(1, reads);
        Assert.Null(noSelection.ObservedAt);
        Assert.Equal(3, Assert.Single(noSelection.Devices).Fields.Count);
        Assert.Equal("object", noSelection.FindField(new(identity, "storage_all_ata_smart_attribute_raw"))!.Definition.ValueKind);
        Assert.Null(noSelection.FindField(new(identity, "storage_all_ata_smart_attribute_raw"))!.Value);

        var duplicates = new[] { physical, Physical(deviceId: 8) };
        var duplicateOwner = Owner((_, command) => new QueryResult(
            command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? duplicates : [OsDisk()], null),
            (_, _, _, _, _) => throw new InvalidOperationException("ambiguous identity must not read ATA"));
        var duplicate = duplicateOwner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { [identity] = AtaSmartAttributeInformationFields.Normalized }));
        Assert.Null(duplicate.ObservedAt);
        Assert.Null(duplicate.FindField(new(identity, "storage_all_ata_smart_attribute_normalized"))!.Value);
    }

    [Fact]
    public void EmptyAndCanceledRequestsDoNotEnumerateDisks()
    {
        var queries = 0;
        var owner = Owner((_, _) => { queries++; throw new InvalidOperationException("not selected"); },
            (_, _, _, _, _) => throw new InvalidOperationException("not selected"));
        Assert.Same(DeviceInformationSnapshot.Empty,
            owner.ReadAtaSmartAttributeInformationFields(new(new Dictionary<string, AtaSmartAttributeInformationFields>())));
        var selectedNone = new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
        {
            ["first"] = AtaSmartAttributeInformationFields.None,
            ["second"] = AtaSmartAttributeInformationFields.None
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => owner.ReadAtaSmartAttributeInformationFields(
            new(selectedNone, MaximumDeviceCount: 1)));
        Assert.Equal(2, owner.ReadAtaSmartAttributeInformationFields(
            new(selectedNone, MaximumDeviceCount: 2)).Devices.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => owner.ReadAtaSmartAttributeInformationFields(
            new(new Dictionary<string, AtaSmartAttributeInformationFields>(), MaximumDeviceCount: 0)));
        var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => owner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { ["identity"] = AtaSmartAttributeInformationFields.Raw }), canceled.Token));
        Assert.Equal(0, queries);
    }

    private static WindowsStorageOwner Owner(Func<string, string, QueryResult> query,
        Func<int, string, AtaSmartPages, string?, int?, AtaSmartRead?> readAta)
        => new(TimeSpan.FromSeconds(15), TimeProvider.System, _ => BackendMessage.Create(0, 0),
            capabilityQuery: (_, _) => new([], null), query: query, readAta: readAta);

    private static Dictionary<string, object?> Physical(int deviceId = 7)
        => Row(("DeviceId", deviceId), ("FriendlyName", "Recorded ATA disk"), ("SerialNumber", "serial"),
            ("UniqueId", "unique"), ("UniqueIdFormat", 1), ("Size", 100UL), ("BusType", 3));

    private static Dictionary<string, object?> OsDisk()
        => Row(("Number", 23), ("SerialNumber", "serial"), ("UniqueId", "unique"), ("Size", 100UL), ("BusType", 3));

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values)
        => values.ToDictionary(static value => value.Name, static value => value.Value);

    private static byte[] DataPage()
    {
        var page = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(page, 0x1234);
        SetDataSlot(page, 0, 9, 0xA531, 0x64, 0x37, [0x10, 0x20, 0x30, 0x40, 0x50, 0x60], 0x7E);
        SetDataSlot(page, 1, 9, 0xF00D, 0x55, 0x44, [9, 8, 7, 6, 5, 4], 0xA5); // Duplicate IDs remain separate.
        SetDataSlot(page, 2, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], 0);
        Seal(page);
        return page;
    }

    private static byte[] ThresholdPage()
    {
        var page = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(page, 0x1234);
        SetThresholdSlot(page, 0, 9, 0x40, Enumerable.Range(1, 10).Select(static value => (byte)value).ToArray());
        SetThresholdSlot(page, 1, 4, 0x31, Enumerable.Range(11, 10).Select(static value => (byte)value).ToArray());
        SetThresholdSlot(page, 2, 9, 0x29, Enumerable.Range(21, 10).Select(static value => (byte)value).ToArray());
        Seal(page);
        return page;
    }

    private static void SetDataSlot(byte[] page, int slot, byte id, ushort flags, byte current, byte worst, byte[] raw, byte reserved)
    {
        var offset = 2 + slot * 12;
        page[offset] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(offset + 1), flags);
        page[offset + 3] = current;
        page[offset + 4] = worst;
        raw.CopyTo(page.AsSpan(offset + 5, 6));
        page[offset + 11] = reserved;
    }

    private static void SetThresholdSlot(byte[] page, int slot, byte id, byte threshold, byte[] reserved)
    {
        var offset = 2 + slot * 12;
        page[offset] = id;
        page[offset + 1] = threshold;
        reserved.CopyTo(page.AsSpan(offset + 2, 10));
    }

    private static void Seal(byte[] page)
    {
        var sum = 0;
        for (var index = 0; index < 511; index++) sum = (sum + page[index]) & 0xFF;
        page[511] = unchecked((byte)-sum);
    }
}
