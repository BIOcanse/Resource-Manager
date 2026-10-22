using ResourceManager.App.Infrastructure.Monitoring;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Platform;

public sealed partial class HardwareMonitorWmiReader
{
    internal static double? ReadCpuTemperature(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardware)
    {
        var row = SelectCpuTemperatureRow(rows, hardware);
        return row is null ? null : ParseTemperature(row.GetValueOrDefault("Value"));
    }

    internal static IReadOnlyDictionary<string, object?>? SelectCpuTemperatureRow(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardware)
    {
        // Select the sensor identity, not whichever sensor happens to have a
        // value this frame. CCD Tdie is a separate physical reading from Tctl.
        var candidates = rows.Where(row => IsSensorType(row, "Temperature") && IsCpuSensor(row, hardware))
            .Select(row => (Row: row, Name: ReadString(row, "Name") ?? ""))
            .ToArray();
        var controls = candidates.Where(item => item.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
            && !item.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase)).ToArray();
        var packages = candidates.Where(item => item.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
            && !item.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = controls.Length > 0 ? controls : packages.Length > 0 ? packages : candidates;
        return selected.OrderBy(item => ReadString(item.Row, "Identifier"), StringComparer.Ordinal)
            .Select(static item => item.Row).FirstOrDefault();
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
