using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace ResourceManager.App.Infrastructure.SystemHealth.Storage;

public sealed class WindowsStorageHealthReader : IStorageHealthReader
{
    public StorageHealthSnapshot Read()
    {
        var physicalDisks = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, SerialNumber, UniqueId, Size, MediaType, BusType, HealthStatus, OperationalStatus, PhysicalLocation FROM MSFT_PhysicalDisk");
        if (physicalDisks.Error is not null)
        {
            return StorageHealthSnapshot.Unavailable(physicalDisks.Error);
        }

        var reliability = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_StorageReliabilityCounter");
        var reliabilityRows = reliability.Error is null ? reliability.Rows : [];
        var disks = physicalDisks.Rows
            .Where(static row => !string.IsNullOrWhiteSpace(DeviceTopologyWmiUtilities.ReadString(row, "DeviceId")))
            .Select(row => CreateDisk(row, reliabilityRows))
            .OrderBy(static disk => ParseDeviceOrdinal(disk.DeviceId))
            .ToArray();
        return new StorageHealthSnapshot(
            DateTimeOffset.UtcNow,
            true,
            disks,
            reliability.Error);
    }

    private static PhysicalDiskHealthSnapshot CreateDisk(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<Dictionary<string, object?>> reliabilityRows)
    {
        var deviceId = Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceId"))!;
        var friendlyName = Clean(DeviceTopologyWmiUtilities.ReadString(row, "FriendlyName")) ?? $"磁盘 {deviceId}";
        var serialNumber = Clean(DeviceTopologyWmiUtilities.ReadString(row, "SerialNumber"));
        var uniqueId = Clean(DeviceTopologyWmiUtilities.ReadString(row, "UniqueId"));
        var busType = DeviceTopologyWmiUtilities.ReadUInt32(row, "BusType");
        var physicalLocation = Clean(DeviceTopologyWmiUtilities.ReadString(row, "PhysicalLocation"));
        var reliabilityRow = FindReliabilityRow(reliabilityRows, deviceId);
        return new PhysicalDiskHealthSnapshot(
            CreateDeviceKey(uniqueId ?? serialNumber ?? $"{deviceId}|{friendlyName}"),
            deviceId,
            friendlyName,
            serialNumber,
            uniqueId,
            DeviceTopologyWmiUtilities.ReadUInt64(row, "Size"),
            DeviceTopologyWmiUtilities.ReadUInt32(row, "MediaType"),
            busType,
            DeviceTopologyWmiUtilities.ReadUInt32(row, "HealthStatus"),
            ReadUInt32Array(row, "OperationalStatus"),
            physicalLocation,
            IsExternal(busType, physicalLocation),
            ReadDouble(reliabilityRow, "Temperature"),
            ReadDouble(reliabilityRow, "TemperatureMax"),
            ReadDouble(reliabilityRow, "Wear"),
            DeviceTopologyWmiUtilities.ReadUInt64(reliabilityRow, "ReadErrorsUncorrected"),
            DeviceTopologyWmiUtilities.ReadUInt64(reliabilityRow, "WriteErrorsUncorrected"),
            DeviceTopologyWmiUtilities.ReadUInt64(reliabilityRow, "StartStopCycleCount"),
            DeviceTopologyWmiUtilities.ReadUInt64(reliabilityRow, "LoadUnloadCycleCount"),
            DeviceTopologyWmiUtilities.ReadUInt64(reliabilityRow, "ReadLatencyMax"),
            DeviceTopologyWmiUtilities.ReadUInt64(reliabilityRow, "WriteLatencyMax"),
            DeviceTopologyWmiUtilities.ReadUInt64(reliabilityRow, "FlushLatencyMax"));
    }

    internal static Dictionary<string, object?>? FindReliabilityRow(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string deviceId)
    {
        var matches = rows.Where(row => ValueEquals(row, "DeviceId", deviceId)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ValueEquals(IReadOnlyDictionary<string, object?> row, string key, string value)
    {
        return string.Equals(
            Clean(DeviceTopologyWmiUtilities.ReadString(row, key)),
            Clean(value),
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<uint> ReadUInt32Array(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is Array values)
        {
            var result = new List<uint>(values.Length);
            foreach (var item in values)
            {
                try
                {
                    result.Add(Convert.ToUInt32(item));
                }
                catch
                {
                }
            }

            return result;
        }

        return [];
    }

    private static double? ReadDouble(IReadOnlyDictionary<string, object?>? row, string key)
    {
        if (row is null || !row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExternal(uint? busType, string? physicalLocation)
    {
        return busType is 4 or 7 or 12 or 13
            || (!string.IsNullOrWhiteSpace(physicalLocation)
                && (physicalLocation.Contains("external", StringComparison.OrdinalIgnoreCase)
                    || physicalLocation.Contains("USB", StringComparison.OrdinalIgnoreCase)));
    }

    private static string CreateDeviceKey(string value)
    {
        return $"physical-disk:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())))}";
    }

    private static int ParseDeviceOrdinal(string value)
    {
        return int.TryParse(value, out var parsed) ? parsed : int.MaxValue;
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
