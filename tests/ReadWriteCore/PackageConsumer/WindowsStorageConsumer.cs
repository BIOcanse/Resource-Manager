using System.Buffers.Binary;
using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

internal static class WindowsStorageConsumer
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var log = new byte[512];
        BinaryPrimitives.WriteUInt128LittleEndian(log.AsSpan(160, 16), UInt128.MaxValue);
        var health = WindowsNvmeHealthReader.ParseHealthLog(log)!;
        Require(health.WideIntegerValues[MetricFieldCode.NvmeMediaErrors] == UInt128.MaxValue,
            "independent NVMe health retains full 128-bit counter");
        Require(!health.WideIntegerValues.ContainsKey(MetricFieldCode.NvmeDataUnitsRead)
            && health.WideIntegerValues[MetricFieldCode.NvmeHostReadCommands] == 0, "unknown data units differ from valid counter zero");
        log[160] = 0;
        Require(health.BytesValues[MetricFieldCode.NvmeHealthLogRaw][160] == 255, "raw NVMe payload owns its bytes");
        Require(MetricFieldRegistry.Definitions.Single(field => field.Code == MetricFieldCode.NvmeMediaErrors).Name
            == "storage_all_nvme_media_errors_reported", "health uses original canonical field registry");
        var smartResponse = new byte[16 + 512];
        BinaryPrimitives.WriteUInt32LittleEndian(smartResponse, 512);
        for (var index = 0; index < 512; index++) smartResponse[16 + index] = (byte)index;
        var rawSmart = WindowsAtaSmartReader.ParseReadResponse(smartResponse)!;
        Require(rawSmart.SequenceEqual(smartResponse.AsSpan(16).ToArray()), "independent ATA SMART retains raw vendor bytes");
        smartResponse[16] ^= 1;
        Require(rawSmart[0] == 0 && WindowsAtaSmartReader.ParseReadResponse(smartResponse) is null,
            "raw SMART ownership and failed checksum");
        Require(WindowsAtaSmartReader.Read(-1, "recorded", AtaSmartPages.Data, null, null) is null
            && WindowsAtaSmartReader.Read(0, "recorded", AtaSmartPages.None, null, null) is null,
            "invalid/no requested SMART page does not open hardware");

        var identify = new byte[4096];
        BinaryPrimitives.WriteUInt16LittleEndian(identify, 0x1234);
        BinaryPrimitives.WriteUInt32LittleEndian(identify.AsSpan(80), 0x00010200);
        BinaryPrimitives.WriteUInt16LittleEndian(identify.AsSpan(256), 8); // OACS is word 128, byte offset 256.
        BinaryPrimitives.WriteUInt32LittleEndian(identify.AsSpan(516), 7);
        BinaryPrimitives.WriteUInt128LittleEndian(identify.AsSpan(280, 16), UInt128.MaxValue);
        var nvme = WindowsStorageIdentifyReader.ParseNvmeController(identify)!;
        Require(nvme.TotalNvmCapacityBytes == UInt128.MaxValue && nvme.MaximumValidNamespaceId == 7,
            "independent NVMe controller declarations preserve capacity domain and namespace limit");
        var identity = WindowsStorageDeviceIdentity.Create(" recorded-controller ", null);
        var frame = WindowsInformationFieldProjection.Storage([new(identity, StorageIdentifyProtocol.NvmeController, null, nvme)], DateTimeOffset.UtcNow);
        var capacity = frame.FindField(new(identity, "storage_all_nvme_controller_accessible_total_nvm_capacity_reported_bytes"))!;
        Require(InformationFieldProjection.Read(identity, capacity, frame.ObservedAt).Value?.Integer
            == UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), "canonical independent Identify field has exact type/value");
        var failed = WindowsStorageIdentifyReader.ReadFields(identity, -1, StorageIdentifyProtocol.NvmeController, "recorded", null, null);
        Require(failed.ObservedAt is null && failed.Devices.Single().Fields.All(field => field.Value is null),
            "unavailable Identify cannot expose stale declarations");

        var pnp = WindowsDeviceTopologyNativeReader.ReadSnapshot();
        Require(pnp.Error is null && pnp.Devices.Count > 0, "actual independent native PnP enumeration");
        var disks = new WindowsDiskLayoutInformationReader().ReadFields();
        Require(disks.ObservedAt is not null && disks.Devices.Count > 0
            && disks.Devices.All(device => device.ObjectType == "os_disk" && device.Fields.Count == 20),
            "actual independent OS disk enumeration and canonical layout/geometry fields");
        var reportedNumbers = disks.Devices.Count(device => device.Fields.Single(field => field.Definition.Name
            == "storage_all_os_disk_number_reported").Value is not null);
        Console.WriteLine($"ReadWriteCore storage consumer passed: exact recorded Identify/SMART/NVMe, actual PnP {pnp.Devices.Count}, OS disks {disks.Devices.Count}, confirmed disk numbers {reportedNumbers}; no SMART hardware command executed.");
    }

    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); }
}
