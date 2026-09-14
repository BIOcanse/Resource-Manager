using ResourceManager.App.Application.Metrics;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class HardwareMonitorWmiMonitoringZone
{
    private static bool IsSensorType(IReadOnlyDictionary<string, object?> row, string sensorType)
    {
        return string.Equals(ReadString(row, "SensorType"), sensorType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMemoryTemperatureSensor(IReadOnlyDictionary<string, object?> row)
    {
        var text = RowText(row);
        return text.Contains("/ram", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dimm", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dram", StringComparison.OrdinalIgnoreCase)
            || text.Contains("spd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMotherboardTemperatureSensor(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        if (!IsMotherboardSensor(row, hardwareByIdentifier)
            || IsVrmTemperatureSensor(row, hardwareByIdentifier)
            || IsChipsetTemperatureSensor(row, hardwareByIdentifier))
        {
            return false;
        }

        var text = RowText(row);
        if (ContainsGpuText(text) || ContainsCpuText(text) || ContainsMemoryText(text) || ContainsStorageText(text))
        {
            return false;
        }

        return true;
    }

    private static bool IsVrmTemperatureSensor(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        var text = RowText(row);
        if (ContainsGpuText(text) || ContainsMemoryText(text) || ContainsStorageText(text))
        {
            return false;
        }

        return IsMotherboardSensor(row, hardwareByIdentifier)
            && (text.Contains("vrm", StringComparison.OrdinalIgnoreCase)
                || text.Contains("mosfet", StringComparison.OrdinalIgnoreCase)
                || text.Contains("mos", StringComparison.OrdinalIgnoreCase)
                || text.Contains("voltage regulator", StringComparison.OrdinalIgnoreCase)
                || text.Contains("vcore", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsChipsetTemperatureSensor(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        var text = RowText(row);
        if (ContainsGpuText(text) || ContainsMemoryText(text) || ContainsStorageText(text))
        {
            return false;
        }

        return IsMotherboardSensor(row, hardwareByIdentifier)
            && (text.Contains("chipset", StringComparison.OrdinalIgnoreCase)
                || text.Contains("pch", StringComparison.OrdinalIgnoreCase)
                || text.Contains("southbridge", StringComparison.OrdinalIgnoreCase)
                || text.Contains("northbridge", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMotherboardVoltageSensor(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        var text = RowText(row);
        if (!IsMotherboardSensor(row, hardwareByIdentifier)
            || ContainsGpuText(text)
            || ContainsCpuText(text)
            || ContainsMemoryText(text)
            || ContainsStorageText(text))
        {
            return false;
        }

        return text.Contains("12v", StringComparison.OrdinalIgnoreCase)
            || text.Contains("+12", StringComparison.OrdinalIgnoreCase)
            || text.Contains("5v", StringComparison.OrdinalIgnoreCase)
            || text.Contains("+5", StringComparison.OrdinalIgnoreCase)
            || text.Contains("3.3", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vbat", StringComparison.OrdinalIgnoreCase)
            || text.Contains("avcc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMotherboardSensor(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> hardwareByIdentifier)
    {
        var text = RowText(row);
        var parent = ReadString(row, "Parent");
        if (!string.IsNullOrWhiteSpace(parent) && hardwareByIdentifier.TryGetValue(parent, out var hardware))
        {
            var hardwareText = $"{hardware.Identifier} {hardware.Name} {hardware.HardwareType}";
            if (hardwareText.Contains("motherboard", StringComparison.OrdinalIgnoreCase)
                || hardwareText.Contains("mainboard", StringComparison.OrdinalIgnoreCase)
                || hardwareText.Contains("superio", StringComparison.OrdinalIgnoreCase)
                || hardwareText.Contains("lpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return text.Contains("motherboard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mainboard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("board", StringComparison.OrdinalIgnoreCase)
            || text.Contains("system", StringComparison.OrdinalIgnoreCase)
            || text.Contains("superio", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/lpc", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vrm", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mosfet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("chipset", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCpuFanSensor(IReadOnlyDictionary<string, object?> row)
    {
        var text = RowText(row);
        return text.Contains("/cpu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("processor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGpuFanSensor(IReadOnlyDictionary<string, object?> row)
    {
        return ContainsGpuText(RowText(row));
    }

    private static bool ContainsGpuText(string text)
    {
        return text.Contains("/gpu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gpu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || text.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("geforce", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rtx", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gtx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCpuText(string text)
    {
        return text.Contains("/cpu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("processor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("core", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tdie", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tctl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMemoryText(string text)
    {
        return text.Contains("/ram", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dimm", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dram", StringComparison.OrdinalIgnoreCase)
            || text.Contains("spd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsStorageText(string text)
    {
        return text.Contains("/hdd", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/ssd", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nvme", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ssd", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hdd", StringComparison.OrdinalIgnoreCase)
            || text.Contains("disk", StringComparison.OrdinalIgnoreCase)
            || text.Contains("drive", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatHardwareMonitorDetail(string provider, IReadOnlyDictionary<string, object?> row)
    {
        var name = ReadString(row, "Name");
        return MetricDisplayDetailFormatter.SensorDetail(name, "硬件传感器");
    }

    private static string RowText(IReadOnlyDictionary<string, object?> row)
    {
        return string.Join(
            " ",
            new[]
            {
                ReadString(row, "Identifier"),
                ReadString(row, "Name"),
                ReadString(row, "Parent")
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }
}
