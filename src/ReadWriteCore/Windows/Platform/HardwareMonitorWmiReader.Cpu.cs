using ResourceManager.App.Infrastructure.Monitoring;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Platform;

public sealed partial class HardwareMonitorWmiReader
{
    internal static double? ReadCpuTemperature(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardware)
    {
        var candidates = rows.Where(row => IsSensorType(row, "Temperature") && IsCpuSensor(row, hardware))
            .Select(row => (Value: ParseTemperature(row.GetValueOrDefault("Value")), Name: ReadString(row, "Name") ?? ""))
            .Where(item => item.Value is not null).ToArray();
        var packages = candidates.Where(item => item.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = packages.Length > 0 ? packages : candidates;
        return selected.Length > 0 ? selected.Max(item => item.Value) : null;
    }

    private static bool IsCpuSensor(IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardware)
    {
        var parent = ReadString(row, "Parent") ?? "";
        return hardware.TryGetValue(parent, out var owner)
            ? owner.HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase)
            : parent.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase)
                || parent.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase);
    }
}
