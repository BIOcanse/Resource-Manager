using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsStorageSensorsMonitoringZone
{
    internal StorageSensorSnapshot ReadSensors()
    {
        return CanRead ? ReadStorageSensors() : StorageSensorsFrozen();
    }

    private static StorageSensorSnapshot StorageSensorsFrozen()
    {
        return new StorageSensorSnapshot(
            new HardwareSensorProviderState(
                "Windows Storage Reliability Counter",
                "Frozen",
                "Windows Storage 传感监控源当前处于功能区冻结。"),
            []);
    }

    private static StorageSensorSnapshot ReadStorageSensors()
    {
        var physicalDisks = QueryObjects(
            @"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, SerialNumber, UniqueId, Size FROM MSFT_PhysicalDisk");

        if (physicalDisks.Error is not null)
        {
            return new StorageSensorSnapshot(
                new HardwareSensorProviderState(
                    "Windows Storage Reliability Counter",
                    "Unavailable",
                    physicalDisks.Error),
                []);
        }

        var disks = physicalDisks.Rows
            .Select(static row => new PhysicalDiskInfo(
                ParseInt(row.GetValueOrDefault("DeviceId")) ?? 0,
                ReadString(row, "DeviceId") ?? string.Empty,
                ReadString(row, "FriendlyName") ?? "Disk",
                ReadString(row, "SerialNumber"),
                ReadString(row, "UniqueId"),
                ParseUlong(row.GetValueOrDefault("Size"))))
            .OrderBy(static disk => disk.Index)
            .ToArray();

        if (disks.Length == 0)
        {
            return new StorageSensorSnapshot(
                new HardwareSensorProviderState(
                    "Windows Storage Reliability Counter",
                    "Unavailable",
                    "未读取到 Windows 物理磁盘列表。"),
                []);
        }

        var counters = QueryObjects(
            @"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_StorageReliabilityCounter");

        if (counters.Error is not null)
        {
            return new StorageSensorSnapshot(
                new HardwareSensorProviderState(
                    "Windows Storage Reliability Counter",
                    "Unavailable",
                    counters.Error),
                disks.Select(disk => CreateDiskSensor(disk, null, "Windows Storage Reliability Counter 不可用。")).ToArray());
        }

        var rows = counters.Rows;
        var sensors = disks
            .Select((disk, ordinal) =>
            {
                var row = FindReliabilityCounter(rows, disk, ordinal);
                var temperature = row is null ? null : ParseTemperature(row.GetValueOrDefault("Temperature"));
                var detail = row is null
                    ? "未找到匹配的 MSFT_StorageReliabilityCounter。"
                    : "Windows Storage Reliability Counter";
                return CreateDiskSensor(disk, temperature, detail);
            })
            .ToArray();

        var state = sensors.Any(static sensor => sensor.TemperatureCelsius is not null)
            ? new HardwareSensorProviderState("Windows Storage Reliability Counter", "Active", "已读取磁盘温度。")
            : new HardwareSensorProviderState("Windows Storage Reliability Counter", "Unavailable", "驱动或权限未返回磁盘温度。");

        return new StorageSensorSnapshot(state, sensors);
    }
    private static DiskTemperatureSensor CreateDiskSensor(PhysicalDiskInfo disk, double? temperature, string detail)
    {
        return new DiskTemperatureSensor(
            disk.Index,
            disk.FriendlyName,
            temperature,
            MetricDisplayDetailFormatter.DiskDetail(disk.FriendlyName, disk.SizeBytes));
    }

    private static Dictionary<string, object?>? FindReliabilityCounter(
        IReadOnlyList<Dictionary<string, object?>> counters,
        PhysicalDiskInfo disk,
        int ordinal)
    {
        return counters.FirstOrDefault(row => ValueMatches(row, "DeviceId", disk.DeviceId))
            ?? counters.FirstOrDefault(row => ValueContains(row, "SerialNumber", disk.SerialNumber))
            ?? counters.FirstOrDefault(row => ValueContains(row, "UniqueId", disk.UniqueId))
            ?? counters.FirstOrDefault(row => ValueContains(row, "InstanceName", disk.SerialNumber))
            ?? counters.FirstOrDefault(row => ValueContains(row, "InstanceName", disk.UniqueId))
            ?? counters.ElementAtOrDefault(ordinal);
    }
    private sealed record PhysicalDiskInfo(
        int Index,
        string DeviceId,
        string FriendlyName,
        string? SerialNumber,
        string? UniqueId,
        ulong? SizeBytes);

}

internal sealed record StorageSensorSnapshot(
    HardwareSensorProviderState ProviderState,
    IReadOnlyList<DiskTemperatureSensor> Disks);
