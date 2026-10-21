using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Platform;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed partial class WindowsStorageOwner
{
    public StorageOwnerSensorSnapshot ReadAtaSensorsCore(AtaSmartSensorReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Devices.Count == 0) return new(new("Windows ATA SMART", "NotRequested", ""), []);
        var physical = query(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, SerialNumber, UniqueId, UniqueIdFormat, Size, BusType FROM MSFT_PhysicalDisk");
        var osDisks = query(@"root\Microsoft\Windows\Storage", "SELECT Number, SerialNumber, UniqueId, Size, BusType FROM MSFT_Disk");
        return ReadAtaSensorsCore(request, physical, osDisks);
    }

    private StorageOwnerSensorSnapshot ReadAtaSensorsCore(AtaSmartSensorReadRequest request,
        QueryResult physical, QueryResult osDisks)
    {
        if (physical.Error is not null || osDisks.Error is not null)
            return new(new("Windows ATA SMART", "Unavailable", physical.Error ?? osDisks.Error!), [])
                { Identities = physical.Error is null ? ProjectIdentities(physical.Rows) : null };
        var identities = ProjectIdentities(physical.Rows);
        var sensors = new List<DiskTemperatureSensor>();
        foreach (var (index, selected) in request.Devices)
        {
            if (selected.Pages == AtaSmartPages.None || identities.Count(identity => identity.DisplayIndex == index
                    && identity.ExactIdentityKey == selected.DeviceIdentity && identity.SupportsAtaSmart) != 1) continue;
            var disk = physical.Rows.Single(row => ParseInt(row.GetValueOrDefault("DeviceId")) == index);
            var serial = ReadString(disk, "SerialNumber")?.Trim();
            if (string.IsNullOrWhiteSpace(serial)
                || physical.Rows.Count(row => Equal(ReadString(row, "SerialNumber"), serial)) != 1) continue;
            var number = FindAtaDiskNumber(disk, osDisks.Rows);
            if (number is null) continue;
            var result = readAta(number.Value, serial, selected.Pages, ReadString(disk, "UniqueId")?.Trim(),
                ParseInt(disk.GetValueOrDefault("UniqueIdFormat")));
            if (result is null) continue;
            sensors.Add(new(index, ReadString(disk, "FriendlyName") ?? "Disk", null, "ATA SMART uninterpreted raw pages")
            { IdentityKey = selected.DeviceIdentity, BytesValues = result.BytesValues });
        }
        return new(new("Windows ATA SMART", sensors.Any(sensor => sensor.BytesValues.Count > 0) ? "Active" : "Unavailable", ""), sensors)
        { Identities = identities };
    }

    public static int? FindAtaDiskNumber(IReadOnlyDictionary<string, object?> physical,
        IReadOnlyList<Dictionary<string, object?>> osDisks)
    {
        var serial = ReadString(physical, "SerialNumber")?.Trim();
        var unique = ReadString(physical, "UniqueId")?.Trim();
        var size = ParseUlong(physical.GetValueOrDefault("Size"));
        if (!WindowsAtaSmartReader.IsAtaBus(ParseInt(physical.GetValueOrDefault("BusType")))
            || string.IsNullOrWhiteSpace(serial) || size is null or 0) return null;
        if (osDisks.Count(row => Equal(ReadString(row, "SerialNumber"), serial)) != 1) return null;
        var matches = osDisks.Where(row => WindowsAtaSmartReader.IsAtaBus(ParseInt(row.GetValueOrDefault("BusType")))
            && Equal(ReadString(row, "SerialNumber"), serial)
            && (string.IsNullOrWhiteSpace(unique) || Equal(ReadString(row, "UniqueId"), unique))
            && ParseUlong(row.GetValueOrDefault("Size")) == size).ToArray();
        if (matches.Length != 1 || ParseInt(matches[0].GetValueOrDefault("Number")) is not { } number || number < 0) return null;
        return osDisks.Count(row => ParseInt(row.GetValueOrDefault("Number")) == number) == 1 ? number : null;
    }
}

