using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class HardwareMonitorWmiMonitoringZone
{
    private static double? ReadPreferredSensorValue(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string sensorType,
        params string[] preferredTerms)
    {
        var candidates = rows
            .Where(row => IsSensorType(row, sensorType))
            .Select(row => new
            {
                Row = row,
                Value = ParseDouble(row.GetValueOrDefault("Value")),
                Text = RowText(row)
            })
            .Where(static item => item.Value is not null)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        foreach (var term in preferredTerms)
        {
            var preferred = candidates.FirstOrDefault(item => item.Text.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return SanitizeSensorValue(sensorType, preferred.Value);
            }
        }

        return SanitizeSensorValue(sensorType, candidates.Max(static item => item.Value!.Value));
    }

    private static PlatformSensorReading? ReadPreferredMotherboardSensor(
        string provider,
        IReadOnlyList<Dictionary<string, object?>> rows,
        string fallbackName,
        Func<IReadOnlyDictionary<string, object?>, bool> predicate,
        params string[] preferredTerms)
    {
        var candidates = rows
            .Where(row => predicate(row))
            .Select(row => new
            {
                Row = row,
                Value = SanitizeSensorValue(ReadString(row, "SensorType") ?? string.Empty, ParseDouble(row.GetValueOrDefault("Value"))),
                Text = RowText(row)
            })
            .Where(static item => item.Value is not null)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        foreach (var term in preferredTerms)
        {
            var preferred = candidates.FirstOrDefault(item => item.Text.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return CreatePlatformSensorReading(provider, preferred.Row, preferred.Value, fallbackName);
            }
        }

        var selected = candidates
            .OrderByDescending(static item => item.Value!.Value)
            .First();
        return CreatePlatformSensorReading(provider, selected.Row, selected.Value, fallbackName);
    }

    private static PlatformSensorReading CreatePlatformSensorReading(
        string provider,
        IReadOnlyDictionary<string, object?> row,
        double? value,
        string fallbackName)
    {
        var name = MetricDisplayDetailFormatter.SensorDetail(ReadString(row, "Name"), fallbackName);
        return new PlatformSensorReading(
            name,
            value,
            FormatHardwareMonitorDetail(provider, row));
    }

    private static double? SanitizeSensorValue(string sensorType, double? value)
    {
        return sensorType switch
        {
            "Temperature" => value is > 0 and < 150 ? value : null,
            "Fan" => value is > 0 and < 20000 ? value : null,
            "Control" => value is >= 0 and <= 100 ? value : null,
            "Voltage" => value is > 0 and < 20 ? value : null,
            "Current" => value is > 0 and < 1000 ? value : null,
            "Power" => value is > 0 and < 1000 ? value : null,
            _ => value
        };
    }

    private static double? FindMatchingControlPercent(
        IReadOnlyList<Dictionary<string, object?>> controlRows,
        IReadOnlyDictionary<string, object?> fanRow)
    {
        if (controlRows.Count == 0)
        {
            return null;
        }

        var fanParent = ReadString(fanRow, "Parent");
        var fanName = ReadString(fanRow, "Name");
        var candidates = controlRows
            .Where(row => string.Equals(ReadString(row, "Parent"), fanParent, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!string.IsNullOrWhiteSpace(fanName))
        {
            var byName = candidates.FirstOrDefault(row =>
                RowText(row).Contains(fanName, StringComparison.OrdinalIgnoreCase)
                || (ReadString(row, "Name") is { Length: > 0 } controlName
                    && fanName.Contains(controlName, StringComparison.OrdinalIgnoreCase)));
            if (byName is not null)
            {
                return ParsePercent(byName.GetValueOrDefault("Value"));
            }
        }

        return candidates.Length == 1 ? ParsePercent(candidates[0].GetValueOrDefault("Value")) : null;
    }
}
