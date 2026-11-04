using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed partial class WindowsStorageOwner
{
    /// <summary>Selected physical identities, using the original OS disk matcher and opened identity query.
    /// Returned information is explicit capture; caller publishes through its existing information owner.</summary>
    public DeviceInformationSnapshot ReadTrimFields(StorageSensorReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Devices.Count == 0) return DeviceInformationSnapshot.Empty;
        if (request.Devices.Count > 4096) throw new ArgumentOutOfRangeException(nameof(request));
        var physical = query(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, SerialNumber, UniqueId, UniqueIdFormat, Size, BusType FROM MSFT_PhysicalDisk");
        var osDisks = query(@"root\Microsoft\Windows\Storage", "SELECT Number, SerialNumber, UniqueId, Size, BusType FROM MSFT_Disk");
        var rows = new List<DeviceInformationObject>();
        var identities = physical.Error is null && physical.Rows.Count <= 4096 ? ProjectIdentities(physical.Rows) : [];
        foreach (var (index, identity) in request.Devices)
        {
            bool? enabled = null;
            if (physical.Error is null && osDisks.Error is null && physical.Rows.Count <= 4096 && osDisks.Rows.Count <= 4096
                && identities.Any(item => item.DisplayIndex == index && item.ExactIdentityKey == identity))
            {
                var disk = physical.Rows.Single(row => ParseInt(row.GetValueOrDefault("DeviceId")) == index);
                var serial = ReadString(disk, "SerialNumber")?.Trim();
                if (!string.IsNullOrWhiteSpace(serial) && physical.Rows.Count(row => Equal(ReadString(row, "SerialNumber"), serial)) == 1
                    && FindPropertyDiskNumber(disk, osDisks.Rows, requireNvme: false) is { } number)
                    enabled = readTrim(number, serial, ReadString(disk, "UniqueId")?.Trim(),
                        ParseInt(disk.GetValueOrDefault("UniqueIdFormat")), (uint?)ParseInt(disk.GetValueOrDefault("BusType")));
            }
            rows.Add(new(identity, "physical_disk", [new DeviceInformationField(DeviceInformationFieldRegistry.StorageAllTrimCapability, enabled is { } value ? System.Text.Json.JsonSerializer.SerializeToElement(value) : (System.Text.Json.JsonElement?)null)]));
        }
        return new(physical.Error is null && osDisks.Error is null ? DateTimeOffset.UtcNow : null, rows);
    }
}
