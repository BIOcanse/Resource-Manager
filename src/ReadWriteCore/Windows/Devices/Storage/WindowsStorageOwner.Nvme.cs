using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed partial class WindowsStorageOwner
{
    public StorageOwnerSensorSnapshot ReadNvmeSensorsCore(StorageSensorReadRequest request)
    {
        if (request.Devices.Count == 0) return new(new("Windows NVMe health", "NotRequested", ""), []);
        var physical = query(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, SerialNumber, UniqueId, UniqueIdFormat, Size, BusType FROM MSFT_PhysicalDisk");
        var osDisks = query(@"root\Microsoft\Windows\Storage", "SELECT Number, SerialNumber, UniqueId, Size, BusType FROM MSFT_Disk");
        if (physical.Error is not null || osDisks.Error is not null)
            return new(new("Windows NVMe health", "Unavailable", physical.Error ?? osDisks.Error!), [])
            { Identities = physical.Error is null ? ProjectIdentities(physical.Rows) : null };
        var sensors = new List<DiskTemperatureSensor>();
        foreach (var (index, identity) in request.Devices)
        {
            var matches = physical.Rows.Where(row => ParseInt(row.GetValueOrDefault("DeviceId")) == index
                && WindowsStorageDeviceIdentity.Create(ReadString(row, "UniqueId"), ReadString(row, "SerialNumber")) == identity).ToArray();
            if (matches.Length != 1 || physical.Rows.Count(row =>
                    WindowsStorageDeviceIdentity.Create(ReadString(row, "UniqueId"), ReadString(row, "SerialNumber")) == identity) != 1
                || physical.Rows.Count(row => ParseInt(row.GetValueOrDefault("DeviceId")) == index) != 1) continue;
            var disk = matches[0];
            if (physical.Rows.Count(row => Equal(ReadString(row, "SerialNumber"), ReadString(disk, "SerialNumber"))) != 1) continue;
            var number = FindNvmeDiskNumber(disk, osDisks.Rows);
            if (number is null) continue;
            var result = readNvme(number.Value, ReadString(disk, "SerialNumber")!.Trim(),
                ReadString(disk, "UniqueId")?.Trim(), ParseInt(disk.GetValueOrDefault("UniqueIdFormat")));
            if (result is null) continue;
            sensors.Add(new(index, ReadString(disk, "FriendlyName") ?? "Disk", null, "Windows NVMe global health log")
            {
                IdentityKey = identity,
                Values = result.Values,
                IntegerValues = result.IntegerValues,
                WideIntegerValues = result.WideIntegerValues,
                BytesValues = result.BytesValues
            });
        }
        return new(new("Windows NVMe health", sensors.Count > 0 ? "Active" : "Unavailable", ""), sensors)
        { Identities = ProjectIdentities(physical.Rows) };
    }

    public static int? FindNvmeDiskNumber(IReadOnlyDictionary<string, object?> physical,
        IReadOnlyList<Dictionary<string, object?>> osDisks)
        => FindPropertyDiskNumber(physical, osDisks, requireNvme: true);

    private static int? FindPropertyDiskNumber(IReadOnlyDictionary<string, object?> physical,
        IReadOnlyList<Dictionary<string, object?>> osDisks, bool requireNvme)
    {
        var serial = ReadString(physical, "SerialNumber")?.Trim();
        var unique = ReadString(physical, "UniqueId")?.Trim();
        var bus = ParseInt(physical.GetValueOrDefault("BusType"));
        if ((requireNvme ? bus != 17 : bus is null or < 0)
            || (!requireNvme && ParseUlong(physical.GetValueOrDefault("Size")) is null or 0)
            || string.IsNullOrWhiteSpace(serial)) return null;
        if (osDisks.Count(row => Equal(ReadString(row, "SerialNumber"), serial)) != 1) return null;
        var matches = osDisks.Where(row => ParseInt(row.GetValueOrDefault("BusType")) == bus
            && Equal(ReadString(row, "SerialNumber"), serial)
            && (string.IsNullOrWhiteSpace(unique) || Equal(ReadString(row, "UniqueId"), unique))
            && ParseUlong(row.GetValueOrDefault("Size")) == ParseUlong(physical.GetValueOrDefault("Size"))).ToArray();
        if (matches.Length != 1 || ParseInt(matches[0].GetValueOrDefault("Number")) is not { } number || number < 0) return null;
        // A duplicated disk number is not a safe handle target either.
        return osDisks.Count(row => ParseInt(row.GetValueOrDefault("Number")) == number) == 1 ? number : null;
    }

    private static bool Equal(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

