using System.Buffers.Binary;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Platform;

internal static class AtaSmartAttributeInformationConsumer
{
    internal static async Task RunRecordedAsync()
    {
        var physical = Row(("DeviceId", 7), ("FriendlyName", "Recorded ATA disk"), ("SerialNumber", "serial"),
            ("UniqueId", "unique"), ("UniqueIdFormat", 1), ("Size", 100UL), ("BusType", 3));
        var osDisk = Row(("Number", 23), ("SerialNumber", "serial"), ("UniqueId", "unique"),
            ("Size", 100UL), ("BusType", 3));
        var reads = new List<AtaSmartPages>();
        var dataPageAvailable = true;
        var thresholdPageAvailable = true;
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), TimeProvider.System,
            _ => BackendMessage.Create(0, 0), capabilityQuery: (_, _) => new([], null),
            query: (_, command) => new QueryResult([
                command.Contains("FROM MSFT_PhysicalDisk", StringComparison.Ordinal) ? physical : osDisk
            ], null),
            readAta: (number, serial, pages, unique, format) =>
            {
                Require(number == 23 && serial == "serial" && unique == "unique" && format == 1,
                    "selected ATA read uses the exact recorded physical/OS identity");
                reads.Add(pages);
                var values = new Dictionary<ResourceManager.App.Domain.Metrics.MetricFieldCode, byte[]>();
                if ((pages & AtaSmartPages.Data) != 0 && dataPageAvailable)
                    values[ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartDataRaw] = DataPage();
                if ((pages & AtaSmartPages.Thresholds) != 0 && thresholdPageAvailable)
                    values[ResourceManager.App.Domain.Metrics.MetricFieldCode.AtaSmartThresholdsRaw] = ThresholdPage();
                return new(values);
            });

        var identity = WindowsStorageOwner.ProjectIdentities([physical]).Single().ExactIdentityKey;
        var allFields = AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw
            | AtaSmartAttributeInformationFields.Threshold;
        var current = owner.ReadAtaSmartAttributeInformationFields(new(
            new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal) { [identity] = allFields }));
        var backend = new InformationReadWriteBackend(() => current,
            _ => ValueTask.FromResult(current), _ => new OperationSupport(true, null, []),
            (selection, token) =>
            {
                token.ThrowIfCancellationRequested();
                var selectedDevices = selection.GroupBy(static item => item.Field.DeviceIdentity, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key,
                        group => group.Aggregate(AtaSmartAttributeInformationFields.None,
                            static (fields, item) => fields | FieldsFor(item.Definition)), StringComparer.Ordinal);
                current = owner.ReadAtaSmartAttributeInformationFields(new(selectedDevices), token);
                return ValueTask.FromResult(current);
            });
        using var client = new HttpClient();
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));

        var rawField = new FieldReference(identity, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw.Name);
        Require(core.Query(rawField).Read.Supported, "public canonical structured field capability");
        var cached = core.ReadCached(rawField);
        var rawStructure = cached.Value?.Structure;
        Require(cached.Status == FieldObservationStatus.Current && rawStructure is { ValueKind: System.Text.Json.JsonValueKind.Object },
            "public cache reads a structured per-disk attribute table");
        var rawSlots = rawStructure!.Value.GetProperty("slots");
        Require(rawSlots.GetArrayLength() == 30
            && rawSlots[0].GetProperty("rawSixBytes").GetString() == Convert.ToBase64String([0x21, 0x22, 0x23, 0x24, 0x25, 0x26]),
            "public raw field retains the exact six-byte attribute payload and ordered slots");

        var normalizedField = new FieldReference(identity, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeNormalized.Name);
        var thresholdField = new FieldReference(identity, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeThreshold.Name);
        var collected = await core.CollectAsync([normalizedField]);
        var normalized = AssertCurrentStructure(collected.Single());
        Require(normalized.GetProperty("slots").GetArrayLength() == 30
            && normalized.GetProperty("slots")[0].GetProperty("currentNormalized").GetByte() == 0x65,
            "public direct collection returns the reported normalized slot value");

        thresholdPageAvailable = false;
        var thresholdFailure = await core.CollectAsync([normalizedField, rawField, thresholdField]);
        var normalizedWithFailedThreshold = AssertCurrentStructure(
            thresholdFailure.Single(reading => reading.Field.FieldName == normalizedField.FieldName));
        var rawWithFailedThreshold = AssertCurrentStructure(
            thresholdFailure.Single(reading => reading.Field.FieldName == rawField.FieldName));
        var unavailableThreshold = thresholdFailure.Single(reading => reading.Field.FieldName == thresholdField.FieldName);
        Require(normalizedWithFailedThreshold.GetProperty("slots")[0].GetProperty("currentNormalized").GetByte() == 0x65
            && rawWithFailedThreshold.GetProperty("slots")[0].GetProperty("rawSixBytes").GetString()
                == Convert.ToBase64String([0x21, 0x22, 0x23, 0x24, 0x25, 0x26])
            && unavailableThreshold.Status == FieldObservationStatus.Unavailable && unavailableThreshold.Value?.Structure is null,
            "public selected capture keeps normalized and raw data when the threshold page is unavailable");

        dataPageAvailable = false;
        thresholdPageAvailable = true;
        var dataFailure = await core.CollectAsync([normalizedField, rawField, thresholdField]);
        var unavailableNormalized = dataFailure.Single(reading => reading.Field.FieldName == normalizedField.FieldName);
        var unavailableRaw = dataFailure.Single(reading => reading.Field.FieldName == rawField.FieldName);
        var thresholdAfterDataFailure = AssertCurrentStructure(
            dataFailure.Single(reading => reading.Field.FieldName == thresholdField.FieldName));
        Require(unavailableNormalized.Status == FieldObservationStatus.Unavailable
            && unavailableNormalized.Value?.Structure is null
            && unavailableRaw.Status == FieldObservationStatus.Unavailable
            && unavailableRaw.Value?.Structure is null
            && thresholdAfterDataFailure.GetProperty("slots")[0].GetProperty("threshold").GetByte() == 0x40,
            "public selected capture keeps the threshold table when the data page is unavailable");

        Require(reads.SequenceEqual([AtaSmartPages.Data | AtaSmartPages.Thresholds, AtaSmartPages.Data,
                AtaSmartPages.Data | AtaSmartPages.Thresholds, AtaSmartPages.Data | AtaSmartPages.Thresholds]),
            "selected consumer request reuses only the existing ATA data/threshold page reader");
        Console.WriteLine("ReadWriteCore ATA SMART consumer passed: recorded exact disk binding, selected structured tables, exact raw bytes and independent data/threshold availability; no ATA device I/O executed.");
    }

    private static System.Text.Json.JsonElement AssertCurrentStructure(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.Value?.Structure is { } structure,
            "public field result is current and structured");
        return reading.Value!.Structure!.Value;
    }

    private static AtaSmartAttributeInformationFields FieldsFor(DeviceInformationDefinition definition)
        => definition == DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeNormalized
            ? AtaSmartAttributeInformationFields.Normalized
            : definition == DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw
                ? AtaSmartAttributeInformationFields.Raw
                : definition == DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeThreshold
                    ? AtaSmartAttributeInformationFields.Threshold
                    : throw new InvalidOperationException("Unexpected field definition.");

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values)
        => values.ToDictionary(static value => value.Name, static value => value.Value, StringComparer.OrdinalIgnoreCase);

    private static byte[] DataPage()
    {
        var page = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(page, 0x1234);
        var entry = page.AsSpan(2, 12);
        entry[0] = 9;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[1..], 0xA531);
        entry[3] = 0x65;
        entry[4] = 0x41;
        new byte[] { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26 }.CopyTo(entry[5..11]);
        entry[11] = 0x7E;
        Seal(page);
        return page;
    }

    private static byte[] ThresholdPage()
    {
        var page = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(page, 0x1234);
        page[2] = 9;
        page[3] = 0x40;
        Seal(page);
        return page;
    }

    private static void Seal(byte[] page)
    {
        var sum = 0;
        for (var index = 0; index < 511; index++) sum = (sum + page[index]) & 0xFF;
        page[511] = unchecked((byte)-sum);
    }

    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); }
}
