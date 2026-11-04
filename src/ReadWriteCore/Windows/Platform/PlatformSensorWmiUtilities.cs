using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Windows.Platform;

public static class PlatformSensorWmiUtilities
{
    [RuntimeReadTransport]
    public static QueryResult QueryObjects(string path, string query, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WMI queries require Windows.");
        try
        {
            var scope = new ManagementScope(path);
            var options = new System.Management.EnumerationOptions
            {
                ReturnImmediately = true,
                Timeout = timeout
            };
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), options);
            using var collection = searcher.Get();
            var rows = new List<Dictionary<string, object?>>();
            foreach (ManagementBaseObject item in collection)
            {
                using var ownedItem = item;
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in item.Properties)
                {
                    row[property.Name] = property.Value;
                }

                rows.Add(row);
            }

            return new QueryResult(rows, null);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return new QueryResult([], ex.Message);
        }
    }

    public static bool ValueMatches(IReadOnlyDictionary<string, object?> row, string property, string? expected)
    {
        return !string.IsNullOrWhiteSpace(expected)
            && string.Equals(ReadString(row, property), expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ValueContains(IReadOnlyDictionary<string, object?> row, string property, string? expected)
    {
        var value = ReadString(row, property);
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(expected)
            && value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadString(IReadOnlyDictionary<string, object?> row, string property)
    {
        return row.TryGetValue(property, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    public static int? ParseInt(object? value)
    {
        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    public static ulong? ParseUlong(object? value)
    {
        return ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    public static double? ParseTemperature(object? value)
    {
        var number = ParseDouble(value);
        return number is > 0 and < 150 ? number : null;
    }

    public static double? ParseFanRpm(object? value)
    {
        var number = ParseDouble(value);
        return number is > 0 and < 20000 ? number : null;
    }

    public static double? ParsePercent(object? value)
    {
        var number = ParseDouble(value);
        return number is >= 0 and <= 100 ? number : null;
    }

    public static double? ParseDouble(object? value)
    {
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}

public sealed record QueryResult(IReadOnlyList<Dictionary<string, object?>> Rows, string? Error);
