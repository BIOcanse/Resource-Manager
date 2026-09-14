using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class HardwareMonitorWmiMonitoringZone
{
    private static IReadOnlyList<PlatformGpuSensor> BuildGpuSensorSnapshots(
        string provider,
        IReadOnlyList<Dictionary<string, object?>> sensorRows,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        return sensorRows
            .Where(row => IsGpuHardwareSensor(row, hardwareByIdentifier))
            .GroupBy(static row => ReadString(row, "Parent") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => BuildGpuSensorSnapshot(provider, group.Key, index, group.ToArray(), hardwareByIdentifier))
            .Where(static sensor => sensor.HasAnyValue)
            .ToArray();
    }

    private static PlatformGpuSensor BuildGpuSensorSnapshot(
        string provider,
        string parent,
        int index,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        var hardware = !string.IsNullOrWhiteSpace(parent) && hardwareByIdentifier.TryGetValue(parent, out var info)
            ? info
            : null;
        var name = hardware?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = rows
                .Select(static row => RowText(row))
                .FirstOrDefault(text => ContainsGpuText(text))
                ?? $"GPU sensor {index}";
        }

        return new PlatformGpuSensor(
            index,
            name,
            MetricDisplayDetailFormatter.SensorDetail(name, $"GPU sensor {index}"),
            ReadPreferredSensorValue(rows, "Temperature", "core", "gpu"),
            ReadPreferredSensorValue(rows, "Power", "package", "board", "total", "core"),
            ReadPreferredSensorValue(rows, "Fan", "fan"),
            ReadPreferredSensorValue(rows, "Control", "fan", "control"),
            ReadPreferredSensorValue(rows, "Voltage", "core", "gpu"),
            ReadPreferredSensorValue(rows, "Current", "edc", "tdc", "core", "gpu"));
    }

    private static bool IsGpuHardwareSensor(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        var parent = ReadString(row, "Parent");
        if (!string.IsNullOrWhiteSpace(parent) && hardwareByIdentifier.TryGetValue(parent, out var hardware))
        {
            if (ContainsGpuText($"{hardware.Name} {hardware.HardwareType} {hardware.Identifier}"))
            {
                return true;
            }
        }

        return ContainsGpuText(RowText(row));
    }
}
