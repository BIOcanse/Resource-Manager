using System.Text;
using System.Globalization;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsStorageDeviceCapabilityReader
{
    private static readonly object Gate = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);
    private static DeviceTopologyStorageSnapshot? cachedSnapshot;
    private static DateTimeOffset cachedAt;

    public static DeviceTopologyStorageSnapshot ReadSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DeviceTopologyStorageSnapshot([], "存储能力枚举仅支持 Windows。");
        }

        lock (Gate)
        {
            if (cachedSnapshot is not null && DateTimeOffset.UtcNow - cachedAt < CacheLifetime)
            {
                return cachedSnapshot;
            }

            var snapshot = ReadCore();
            if (snapshot.Error is null)
            {
                cachedSnapshot = snapshot;
                cachedAt = DateTimeOffset.UtcNow;
            }

            return snapshot;
        }
    }

    public static DeviceTopologyStorageDevice? Resolve(
        DeviceTopologyStorageSnapshot snapshot,
        string deviceId,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices)
    {
        var normalized = NormalizeDeviceId(deviceId);
        if (normalized.Length == 0)
        {
            return null;
        }

        var exact = snapshot.Devices.FirstOrDefault(binding =>
            NormalizeDeviceId(binding.PnpDeviceId).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Device;
        }

        if (!CanReceiveRelatedStorage(normalized))
        {
            return null;
        }

        var matches = snapshot.Devices
            .Select(binding => new
            {
                Binding = binding,
                Distance = RelationshipDistance(normalized, NormalizeDeviceId(binding.PnpDeviceId), nativeDevices)
            })
            .Where(static candidate => candidate.Distance is >= 1 and <= 4)
            .OrderBy(static candidate => candidate.Distance)
            .ToArray();
        return matches.Length > 0
            && (matches.Length == 1 || matches[0].Distance < matches[1].Distance)
                ? matches[0].Binding.Device
                : null;
    }

    internal static bool CanReceiveRelatedStorage(string deviceId)
    {
        var normalized = NormalizeDeviceId(deviceId);
        return normalized.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("UASPSTOR\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("1394\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("SBP2\\", StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceTopologyStorageSnapshot ReadCore()
    {
        var disks = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Index, DeviceID, PNPDeviceID, Model, Manufacturer, SerialNumber, FirmwareRevision, MediaType, InterfaceType, Size, BytesPerSector, Status, Partitions FROM Win32_DiskDrive");
        if (disks.Error is not null)
        {
            return new DeviceTopologyStorageSnapshot([], disks.Error);
        }

        var partitions = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT DeviceID, DiskIndex, Index, Name, Size, Type, Bootable, BootPartition, PrimaryPartition, StartingOffset FROM Win32_DiskPartition");
        var logicalDisks = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType, VolumeSerialNumber FROM Win32_LogicalDisk");
        var diskAssociations = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition");
        var logicalAssociations = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
        var storageDisks = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\Microsoft\Windows\Storage",
            "SELECT Number, FriendlyName, SerialNumber, FirmwareVersion, PartitionStyle, BusType, HealthStatus, Size FROM MSFT_Disk");

        var partitionRows = partitions.Rows
            .Where(static row => !string.IsNullOrWhiteSpace(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID")))
            .GroupBy(static row => DeviceTopologyWmiUtilities.ReadString(row, "DeviceID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var logicalRows = logicalDisks.Rows
            .Where(static row => !string.IsNullOrWhiteSpace(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID")))
            .GroupBy(static row => DeviceTopologyWmiUtilities.ReadString(row, "DeviceID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var partitionsByDisk = BuildAssociationMap(diskAssociations.Rows, "Antecedent", "Dependent");
        var logicalByPartition = BuildAssociationMap(logicalAssociations.Rows, "Antecedent", "Dependent");
        var storageByNumber = storageDisks.Rows
            .Where(static row => DeviceTopologyWmiUtilities.ReadUInt32(row, "Number") is not null)
            .GroupBy(static row => DeviceTopologyWmiUtilities.ReadUInt32(row, "Number")!.Value)
            .ToDictionary(static group => group.Key, static group => group.First());

        var result = new List<DeviceTopologyStorageBinding>();
        foreach (var row in disks.Rows)
        {
            var pnpDeviceId = Clean(DeviceTopologyWmiUtilities.ReadString(row, "PNPDeviceID"));
            var physicalDeviceId = Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID"));
            if (pnpDeviceId is null || physicalDeviceId is null)
            {
                continue;
            }

            var diskIndex = DeviceTopologyWmiUtilities.ReadUInt32(row, "Index");
            storageByNumber.TryGetValue(diskIndex ?? uint.MaxValue, out var storageRow);
            var partitionIds = partitionsByDisk.TryGetValue(physicalDeviceId, out var associatedPartitions)
                ? associatedPartitions
                : partitionRows
                    .Where(pair => DeviceTopologyWmiUtilities.ReadUInt32(pair.Value, "DiskIndex") == diskIndex)
                    .Select(static pair => pair.Key)
                    .ToArray();
            var partitionModels = partitionIds
                .Where(partitionRows.ContainsKey)
                .Select(partitionId => CreatePartition(
                    partitionRows[partitionId],
                    logicalByPartition.TryGetValue(partitionId, out var logicalIds) ? logicalIds : [],
                    logicalRows))
                .OrderBy(static partition => partition.StartingOffsetBytes ?? ulong.MaxValue)
                .ToArray();

            var model = new DeviceTopologyStorageDevice(
                physicalDeviceId,
                Clean(DeviceTopologyWmiUtilities.ReadString(storageRow, "FriendlyName"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Model")),
                Clean(DeviceTopologyWmiUtilities.ReadString(row, "Manufacturer")),
                Clean(DeviceTopologyWmiUtilities.ReadString(storageRow, "SerialNumber"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "SerialNumber")),
                Clean(DeviceTopologyWmiUtilities.ReadString(storageRow, "FirmwareVersion"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "FirmwareRevision")),
                Clean(DeviceTopologyWmiUtilities.ReadString(row, "MediaType")),
                DescribeBusType(
                    DeviceTopologyWmiUtilities.ReadUInt32(storageRow, "BusType"),
                    Clean(DeviceTopologyWmiUtilities.ReadString(row, "InterfaceType"))),
                DeviceTopologyWmiUtilities.ReadUInt64(storageRow, "Size")
                    ?? DeviceTopologyWmiUtilities.ReadUInt64(row, "Size"),
                DeviceTopologyWmiUtilities.ReadUInt32(row, "BytesPerSector"),
                DescribePartitionStyle(DeviceTopologyWmiUtilities.ReadUInt32(storageRow, "PartitionStyle")),
                DescribeHealth(
                    DeviceTopologyWmiUtilities.ReadUInt32(storageRow, "HealthStatus"),
                    Clean(DeviceTopologyWmiUtilities.ReadString(row, "Status"))),
                partitionModels,
                BackendMessage.Create(
                    BackendMessageDomains.DeviceTopology,
                    storageRow is null
                        ? BackendMessageCodes.DeviceTopology.SourceWin32DiskAssociations
                        : BackendMessageCodes.DeviceTopology.SourceMsftDiskAssociations));
            result.Add(new DeviceTopologyStorageBinding(NormalizeDeviceId(pnpDeviceId), model));
        }

        var errors = new[]
            {
                partitions.Error,
                logicalDisks.Error,
                diskAssociations.Error,
                logicalAssociations.Error
            }
            .Where(static error => !string.IsNullOrWhiteSpace(error))
            .ToArray();
        return new DeviceTopologyStorageSnapshot(
            result,
            errors.Length == 0 ? null : string.Join("；", errors));
    }

    private static DeviceTopologyStoragePartition CreatePartition(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<string> logicalIds,
        IReadOnlyDictionary<string, Dictionary<string, object?>> logicalRows)
    {
        var index = DeviceTopologyWmiUtilities.ReadUInt32(row, "Index");
        var volumes = logicalIds
            .Where(logicalRows.ContainsKey)
            .Select(logicalId => CreateVolume(logicalRows[logicalId]))
            .ToArray();
        return new DeviceTopologyStoragePartition(
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID"))
                ?? $"PARTITION#{index?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            index is null ? null : index.Value + 1,
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "Type")),
            DeviceTopologyWmiUtilities.ReadUInt64(row, "Size"),
            DeviceTopologyWmiUtilities.ReadUInt64(row, "StartingOffset"),
            DeviceTopologyWmiUtilities.ReadBoolean(row, "Bootable"),
            DeviceTopologyWmiUtilities.ReadBoolean(row, "BootPartition"),
            DeviceTopologyWmiUtilities.ReadBoolean(row, "PrimaryPartition"),
            volumes);
    }

    private static DeviceTopologyStorageVolume CreateVolume(IReadOnlyDictionary<string, object?> row)
    {
        var driveLetter = Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID"));
        var fileSystem = Clean(DeviceTopologyWmiUtilities.ReadString(row, "FileSystem"));
        var size = DeviceTopologyWmiUtilities.ReadUInt64(row, "Size");
        var free = DeviceTopologyWmiUtilities.ReadUInt64(row, "FreeSpace");
        var mountState = DeviceStorageMountStates.Mounted;
        if (driveLetter is not null)
        {
            try
            {
                var drive = new DriveInfo($"{driveLetter}\\");
                mountState = drive.IsReady
                    ? DeviceStorageMountStates.Mounted
                    : DeviceStorageMountStates.NotReady;
                if (drive.IsReady)
                {
                    fileSystem = Clean(drive.DriveFormat) ?? fileSystem;
                    size = checked((ulong)drive.TotalSize);
                    free = checked((ulong)drive.AvailableFreeSpace);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                mountState = DeviceStorageMountStates.Unknown;
            }
        }

        return new DeviceTopologyStorageVolume(
            driveLetter,
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "VolumeName")),
            fileSystem,
            size,
            free,
            mountState,
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "VolumeSerialNumber")));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildAssociationMap(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string parentProperty,
        string childProperty)
    {
        return rows
            .Select(row => new
            {
                Parent = ReadReferenceDeviceId(DeviceTopologyWmiUtilities.ReadString(row, parentProperty)),
                Child = ReadReferenceDeviceId(DeviceTopologyWmiUtilities.ReadString(row, childProperty))
            })
            .Where(static pair => pair.Parent is not null && pair.Child is not null)
            .GroupBy(static pair => pair.Parent!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<string>)group
                    .Select(static pair => pair.Child!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    internal static string? ReadReferenceDeviceId(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        const string marker = "DeviceID=";
        var markerIndex = reference.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var value = reference[(markerIndex + marker.Length)..].Trim();
        if (!value.StartsWith('"'))
        {
            var separator = value.IndexOf(',');
            return Clean(separator < 0 ? value : value[..separator]);
        }

        var builder = new StringBuilder();
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                break;
            }

            if (character == '\\' && index + 1 < value.Length && value[index + 1] is '\\' or '"')
            {
                builder.Append(value[++index]);
                continue;
            }

            builder.Append(character);
        }

        return Clean(builder.ToString());
    }

    private static int? RelationshipDistance(
        string left,
        string right,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices)
    {
        var distance = DistanceToAncestor(left, right, nativeDevices);
        var reverse = DistanceToAncestor(right, left, nativeDevices);
        return distance is null ? reverse : reverse is null ? distance : Math.Min(distance.Value, reverse.Value);
    }

    private static int? DistanceToAncestor(
        string descendant,
        string ancestor,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices)
    {
        var current = descendant;
        for (var distance = 0; distance <= 8 && current.Length > 0; distance++)
        {
            if (current.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
            {
                return distance;
            }

            if (!nativeDevices.TryGetValue(current, out var device) || string.IsNullOrWhiteSpace(device.ParentDeviceId))
            {
                break;
            }

            current = NormalizeDeviceId(device.ParentDeviceId);
        }

        return null;
    }

    private static string? DescribeBusType(uint? value, string? fallback)
    {
        return value switch
        {
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            4 => "IEEE 1394",
            6 => "Fibre Channel",
            7 => "USB",
            8 => "RAID",
            9 => "iSCSI",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            14 => "Virtual Disk",
            15 => "File Backed Virtual Disk",
            16 => "Storage Spaces",
            17 => "NVMe",
            18 => "SCM",
            19 => "UFS",
            20 => "NVMe over Fabrics",
            _ => fallback
        };
    }

    private static string? DescribePartitionStyle(uint? value)
    {
        return value switch
        {
            1 => "MBR",
            2 => "GPT",
            3 => "RAW",
            _ => null
        };
    }

    private static string? DescribeHealth(uint? value, string? fallback)
    {
        return value switch
        {
            0 => DeviceStorageHealthStates.Healthy,
            1 => DeviceStorageHealthStates.Warning,
            2 => DeviceStorageHealthStates.Unhealthy,
            5 => DeviceStorageHealthStates.Unknown,
            _ => fallback
        };
    }

    private static string NormalizeDeviceId(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('"').Replace('/', '\\').ToUpperInvariant();
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('\0');
    }
}

internal sealed record DeviceTopologyStorageSnapshot(
    IReadOnlyList<DeviceTopologyStorageBinding> Devices,
    string? Error);

internal sealed record DeviceTopologyStorageBinding(
    string PnpDeviceId,
    DeviceTopologyStorageDevice Device);
