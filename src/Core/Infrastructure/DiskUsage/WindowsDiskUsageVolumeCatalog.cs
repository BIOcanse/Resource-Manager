using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>
/// 本机卷清单。
///
/// 盘符、文件系统、容量直接来自 <see cref="DriveInfo"/>；
/// 介质来源分三步定：网络/可移动/光驱由驱动器类型直接给出；
/// subst 出来的盘符由 QueryDosDevice 认出来；
/// 剩下的固定盘去存储管理空间查总线类型，虚拟磁盘和存储空间都在总线类型里写明了。
/// 查不到就是 <see cref="DiskUsageVolumeKinds.Unknown"/>，不猜。
/// </summary>
internal sealed class WindowsDiskUsageVolumeCatalog : IDiskUsageVolumeCatalog
{
    // StorageBusType：14 虚拟、15 文件支持的虚拟盘（VHD/VHDX）、16 存储空间、9 iSCSI。
    private static readonly HashSet<uint> VirtualBusTypes = [9, 14, 15, 16];

    public IReadOnlyList<DiskUsageVolume> ReadVolumes()
    {
        var busTypesByLetter = ReadBusTypesByDriveLetter();
        var elevated = IsElevated();
        var volumes = new List<DiskUsageVolume>();
        foreach (var drive in SafeDrives())
        {
            var volumeId = NormalizeVolumeId(drive.Name);
            if (volumeId is null)
            {
                continue;
            }

            var ready = SafeIsReady(drive);
            var fileSystem = ready ? SafeText(() => drive.DriveFormat) : string.Empty;
            volumes.Add(new DiskUsageVolume(
                volumeId,
                ready ? SafeText(() => drive.VolumeLabel) : string.Empty,
                fileSystem,
                ResolveVolumeKind(drive, volumeId, busTypesByLetter),
                ready ? SafeSize(() => drive.TotalSize) : 0,
                ready ? SafeSize(() => drive.AvailableFreeSpace) : 0,
                ready,
                ready
                    && elevated
                    && fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)));
        }

        return volumes
            .OrderBy(static volume => volume.VolumeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveVolumeKind(
        DriveInfo drive,
        string volumeId,
        IReadOnlyDictionary<string, uint> busTypesByLetter)
    {
        var driveType = SafeDriveType(drive);
        switch (driveType)
        {
            case DriveType.Network:
                return DiskUsageVolumeKinds.Network;
            case DriveType.Removable:
                return DiskUsageVolumeKinds.Removable;
            case DriveType.CDRom:
                return DiskUsageVolumeKinds.Optical;
            case DriveType.Ram:
                return DiskUsageVolumeKinds.Virtual;
            case DriveType.Fixed:
                break;
            default:
                return DiskUsageVolumeKinds.Unknown;
        }

        if (IsSubstitutedDrive(volumeId))
        {
            return DiskUsageVolumeKinds.Virtual;
        }

        if (!busTypesByLetter.TryGetValue(volumeId, out var busType))
        {
            return DiskUsageVolumeKinds.Unknown;
        }

        return VirtualBusTypes.Contains(busType)
            ? DiskUsageVolumeKinds.Virtual
            : DiskUsageVolumeKinds.Physical;
    }

    /// <summary>
    /// subst 出来的盘符，它的 DOS 设备目标是 <c>\??\</c> 开头的一个普通目录，
    /// 真盘是 <c>\Device\HarddiskVolumeN</c>。
    /// </summary>
    private static bool IsSubstitutedDrive(string volumeId)
    {
        var target = new StringBuilder(1024);
        if (NativeMethods.QueryDosDeviceW(volumeId, target, target.Capacity) == 0)
        {
            return false;
        }
        return target.ToString().StartsWith(@"\??\", StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, uint> ReadBusTypesByDriveLetter()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var partitions = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\Microsoft\Windows\Storage",
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition");
        var disks = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\Microsoft\Windows\Storage",
            "SELECT Number, BusType FROM MSFT_Disk");
        if (partitions.Error is not null || disks.Error is not null)
        {
            return map;
        }

        var busTypeByDiskNumber = new Dictionary<uint, uint>();
        foreach (var row in disks.Rows)
        {
            var number = DeviceTopologyWmiUtilities.ReadUInt32(row, "Number");
            var busType = DeviceTopologyWmiUtilities.ReadUInt32(row, "BusType");
            if (number is { } diskNumber && busType is { } bus)
            {
                busTypeByDiskNumber[diskNumber] = bus;
            }
        }

        foreach (var row in partitions.Rows)
        {
            var letter = DeviceTopologyWmiUtilities.ReadString(row, "DriveLetter")?.Trim();
            var diskNumber = DeviceTopologyWmiUtilities.ReadUInt32(row, "DiskNumber");
            if (string.IsNullOrEmpty(letter)
                || letter == "\0"
                || diskNumber is not { } number
                || !busTypeByDiskNumber.TryGetValue(number, out var busType))
            {
                continue;
            }
            map[$"{letter[0]}:"] = busType;
        }

        return map;
    }

    /// <summary>盘符统一成 <c>C:</c> 这种形态；认不出来的返回 null。</summary>
    private static string? NormalizeVolumeId(string driveName)
    {
        var trimmed = driveName?.Trim() ?? string.Empty;
        return trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'
            ? $"{char.ToUpperInvariant(trimmed[0])}:"
            : null;
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool SafeIsReady(DriveInfo drive)
        => SafeRead(() => drive.IsReady, false);

    private static DriveType SafeDriveType(DriveInfo drive)
        => SafeRead(() => drive.DriveType, DriveType.Unknown);

    private static string SafeText(Func<string> read)
        => SafeRead(read, string.Empty) ?? string.Empty;

    private static ulong SafeSize(Func<long> read)
    {
        var value = SafeRead(read, 0L);
        return value > 0 ? (ulong)value : 0;
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or DriveNotFoundException
            or ArgumentException)
        {
            return fallback;
        }
    }
}
