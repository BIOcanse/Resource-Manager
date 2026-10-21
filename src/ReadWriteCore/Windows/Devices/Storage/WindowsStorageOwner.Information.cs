using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.App.Infrastructure.Monitoring;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed partial class WindowsStorageOwner
{
    public IReadOnlyList<DeviceTopologyStorageInformation>? ReadInformationDeclarations()
    {
        var physical = query(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, SerialNumber, UniqueId, UniqueIdFormat, Size, BusType FROM MSFT_PhysicalDisk");
        if (physical.Error is not null) return null;
        var identities = ProjectIdentities(physical.Rows)
            .Where(static identity => identity.SupportsAtaSmart || identity.SupportsNvmeHealth).ToArray();
        if (identities.Length == 0) return [];
        var osDisks = query(@"root\Microsoft\Windows\Storage", "SELECT Number, SerialNumber, UniqueId, Size, BusType FROM MSFT_Disk");
        var information = new List<DeviceTopologyStorageInformation>();
        foreach (var identity in identities)
        {
            var protocol = identity.SupportsAtaSmart ? StorageIdentifyProtocol.Ata : StorageIdentifyProtocol.NvmeController;
            StorageIdentifyRead? read = null;
            var disk = physical.Rows.Single(row => ParseInt(row.GetValueOrDefault("DeviceId")) == identity.DisplayIndex);
            var serial = ReadString(disk, "SerialNumber")?.Trim();
            if (osDisks.Error is null && !string.IsNullOrWhiteSpace(serial)
                && ParseUlong(disk.GetValueOrDefault("Size")) is > 0
                && physical.Rows.Count(row => Equal(ReadString(row, "SerialNumber"), serial)) == 1)
            {
                var number = protocol == StorageIdentifyProtocol.Ata
                    ? FindAtaDiskNumber(disk, osDisks.Rows) : FindNvmeDiskNumber(disk, osDisks.Rows);
                if (number is { } n) read = readIdentify(n, protocol, serial, ReadString(disk, "UniqueId")?.Trim(),
                    ParseInt(disk.GetValueOrDefault("UniqueIdFormat")));
            }
            information.Add(new(identity.ExactIdentityKey, protocol,
                protocol == StorageIdentifyProtocol.Ata ? read?.Ata : null,
                protocol == StorageIdentifyProtocol.NvmeController ? read?.Nvme : null)
            {
                HardwareIdentityEvidence = read is null || string.IsNullOrWhiteSpace(serial) ? null
                    : new(serial, ReadString(disk, "UniqueId")?.Trim(), ParseInt(disk.GetValueOrDefault("UniqueIdFormat")))
            });
        }
        return information;
    }
}
