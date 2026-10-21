using System.Globalization;
using ResourceManager.ReadWrite.Windows.Platform;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceTopologyWmiUtilities
{
    public static DeviceTopologyWmiQueryResult QueryObjects(string path, string query)
        => QueryObjects(path, query, TimeSpan.FromSeconds(2));

    public static DeviceTopologyWmiQueryResult QueryObjects(string path, string query, TimeSpan timeout)
    {
        var result = PlatformSensorWmiUtilities.QueryObjects(path, query, timeout);
        return new(result.Rows, result.Error);
    }

    public static string? ReadString(IReadOnlyDictionary<string, object?>? row, string property)
    {
        return row is not null && row.TryGetValue(property, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
    }

    public static IReadOnlyList<string> ReadStringArray(IReadOnlyDictionary<string, object?> row, string property)
    {
        if (!row.TryGetValue(property, out var value) || value is null)
        {
            return [];
        }

        if (value is string single)
        {
            return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
        }

        if (value is string[] values)
        {
            return values
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return [];
    }

    public static ulong? ReadUInt64(IReadOnlyDictionary<string, object?>? row, string property)
    {
        if (row is null || !row.TryGetValue(property, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static uint? ReadUInt32(IReadOnlyDictionary<string, object?>? row, string property)
    {
        var value = ReadUInt64(row, property);
        return value is not null && value <= uint.MaxValue ? (uint)value : null;
    }

    public static bool? ReadBoolean(IReadOnlyDictionary<string, object?>? row, string property)
    {
        if (row is null || !row.TryGetValue(property, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            return null;
        }
    }
}

public sealed record DeviceTopologyWmiQueryResult(IReadOnlyList<Dictionary<string, object?>> Rows, string? Error);
