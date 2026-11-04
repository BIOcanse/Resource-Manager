using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Domain.Metrics;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Platform;

public sealed partial class HardwareMonitorWmiReader
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
                ?? string.Empty;
        }

        return new PlatformGpuSensor(
            index,
            name,
            name,
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
