using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class HardwareMonitorWmiMonitoringZone
{
    internal HardwareMonitorSensorSnapshot ReadSensors()
    {
        return CanRead ? ReadHardwareMonitorSensors() : HardwareMonitorSensorsFrozen();
    }

    private static HardwareMonitorSensorSnapshot HardwareMonitorSensorsFrozen()
    {
        return new HardwareMonitorSensorSnapshot(
            new HardwareSensorProviderState(
                "Hardware monitor WMI",
                "Frozen",
                "HardwareMonitor WMI 监控源当前处于功能区冻结。"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            []);
    }

    private static HardwareMonitorSensorSnapshot ReadHardwareMonitorSensors()
    {
        var libre = QueryHardwareMonitorSensors(@"root\LibreHardwareMonitor");
        if (libre.Rows.Count > 0 || libre.Error is null)
        {
            return BuildHardwareMonitorSnapshot(
                "LibreHardwareMonitor WMI",
                libre,
                QueryHardwareMonitorHardware(@"root\LibreHardwareMonitor"));
        }

        var open = QueryHardwareMonitorSensors(@"root\OpenHardwareMonitor");
        if (open.Rows.Count > 0 || open.Error is null)
        {
            return BuildHardwareMonitorSnapshot(
                "OpenHardwareMonitor WMI",
                open,
                QueryHardwareMonitorHardware(@"root\OpenHardwareMonitor"));
        }

        return new HardwareMonitorSensorSnapshot(
            new HardwareSensorProviderState(
                "Hardware monitor WMI",
                "Unavailable",
                $"{libre.Error}; {open.Error}"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            []);
    }

    private static QueryResult QueryHardwareMonitorSensors(string path)
    {
        return QueryObjects(path, "SELECT Identifier, Name, Parent, SensorType, Value FROM Sensor");
    }

    private static QueryResult QueryHardwareMonitorHardware(string path)
    {
        return QueryObjects(path, "SELECT Identifier, Name, HardwareType FROM Hardware");
    }

    private static HardwareMonitorSensorSnapshot BuildHardwareMonitorSnapshot(
        string provider,
        QueryResult result,
        QueryResult hardware)
    {
        if (result.Error is not null && result.Rows.Count == 0)
        {
            return new HardwareMonitorSensorSnapshot(
                new HardwareSensorProviderState(provider, "Unavailable", result.Error),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                []);
        }

        var hardwareByIdentifier = BuildHardwareMap(hardware.Rows);
        var temperatureRows = result.Rows
            .Where(static row => IsSensorType(row, "Temperature"))
            .ToArray();
        var memoryTemperatures = temperatureRows
            .Where(IsMemoryTemperatureSensor)
            .Select(static row => ParseTemperature(row.GetValueOrDefault("Value")))
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .ToArray();
        var memoryTemperature = memoryTemperatures.Length == 0 ? (double?)null : memoryTemperatures.Max();
        var motherboardTemperature = ReadPreferredMotherboardSensor(
            provider,
            temperatureRows,
            "主板温度",
            row => IsMotherboardTemperatureSensor(row, hardwareByIdentifier),
            "motherboard",
            "mainboard",
            "system",
            "board",
            "temp");
        var vrmTemperature = ReadPreferredMotherboardSensor(
            provider,
            temperatureRows,
            "供电温度",
            row => IsVrmTemperatureSensor(row, hardwareByIdentifier),
            "vrm",
            "mosfet",
            "mos",
            "voltage regulator",
            "vcore");
        var chipsetTemperature = ReadPreferredMotherboardSensor(
            provider,
            temperatureRows,
            "芯片组温度",
            row => IsChipsetTemperatureSensor(row, hardwareByIdentifier),
            "chipset",
            "pch",
            "southbridge",
            "northbridge");
        var motherboardVoltage = ReadPreferredMotherboardSensor(
            provider,
            result.Rows.Where(static row => IsSensorType(row, "Voltage")).ToArray(),
            "主板电压",
            row => IsMotherboardVoltageSensor(row, hardwareByIdentifier),
            "12v",
            "+12v",
            "5v",
            "+5v",
            "3.3v",
            "+3.3v",
            "vbat",
            "avcc");

        var fanRows = result.Rows
            .Where(static row => IsSensorType(row, "Fan"))
            .ToArray();
        var controlRows = result.Rows
            .Where(static row => IsSensorType(row, "Control"))
            .ToArray();
        var unclassifiedFanRows = fanRows
            .Where(static row => !IsCpuFanSensor(row) && !IsGpuFanSensor(row))
            .ToArray();
        var cpuFan = fanRows
            .Where(IsCpuFanSensor)
            .Select(static row => ParseFanRpm(row.GetValueOrDefault("Value")))
            .FirstOrDefault(static value => value is not null)
            ?? (unclassifiedFanRows.Length == 1
                ? ParseFanRpm(unclassifiedFanRows[0].GetValueOrDefault("Value"))
                : null);
        var cpuFanPercent = controlRows
            .Where(IsCpuFanSensor)
            .Select(static row => ParsePercent(row.GetValueOrDefault("Value")))
            .FirstOrDefault(static value => value is not null);
        var systemFans = unclassifiedFanRows
            .Select((row, index) => new SystemFanSensor(
                index,
                MetricDisplayDetailFormatter.FanDetail(ReadString(row, "Name"), index),
                ParseFanRpm(row.GetValueOrDefault("Value")),
                FindMatchingControlPercent(controlRows, row),
                FormatHardwareMonitorDetail(provider, row)))
            .ToArray();
        var gpuSensors = BuildGpuSensorSnapshots(provider, result.Rows, hardwareByIdentifier);

        var state = memoryTemperature is not null
            || motherboardTemperature is { Value: not null }
            || vrmTemperature is { Value: not null }
            || chipsetTemperature is { Value: not null }
            || motherboardVoltage is { Value: not null }
            || cpuFan is not null
            || cpuFanPercent is not null
            || systemFans.Any(static fan => fan.SpeedRpm is not null || fan.SpeedPercent is not null)
            || gpuSensors.Any(static sensor => sensor.HasAnyValue)
                ? new HardwareSensorProviderState(provider, "Active", "已读取内存温度、主板传感、风扇或 GPU 后备传感器。")
                : new HardwareSensorProviderState(provider, "Unavailable", "未发现内存温度、主板传感、风扇或 GPU 后备传感器。");

        return new HardwareMonitorSensorSnapshot(
            state,
            memoryTemperature,
            motherboardTemperature,
            vrmTemperature,
            chipsetTemperature,
            motherboardVoltage,
            cpuFan,
            cpuFanPercent,
            systemFans,
            gpuSensors);
    }

    private static IReadOnlyDictionary<string, HardwareMonitorHardwareInfo> BuildHardwareMap(
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        return rows
            .Select(static row => new HardwareMonitorHardwareInfo(
                ReadString(row, "Identifier") ?? string.Empty,
                ReadString(row, "Name") ?? string.Empty,
                ReadString(row, "HardwareType") ?? string.Empty))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Identifier))
            .GroupBy(static item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

}
