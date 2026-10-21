using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using static ResourceManager.ReadWrite.Windows.Platform.PlatformSensorWmiUtilities;

namespace ResourceManager.ReadWrite.Windows.Platform;

/// <summary>Explicit, uncached WMI reads. Sensor names/details are raw; display policy belongs to the caller.</summary>
public sealed partial class HardwareMonitorWmiReader
{
    private readonly Func<string, string, TimeSpan, QueryResult> query;

    public HardwareMonitorWmiReader(Func<string, string, TimeSpan, QueryResult>? query = null)
        => this.query = query ?? QueryObjects;

    public HardwareMonitorFanIdentityRead ReadFanIdentities(TimeSpan queryTimeout)
    {
        foreach (var provider in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
        {
            var result = query(provider, "SELECT Identifier, Name, Parent, SensorType FROM Sensor", queryTimeout);
            if (result.Error is not null) continue;
            var identities = result.Rows.Where(static row => IsSensorType(row, "Fan") && !IsCpuFanSensor(row) && !IsGpuFanSensor(row))
                .Where(static row => !string.IsNullOrWhiteSpace(ReadString(row, "Identifier")))
                .GroupBy(static row => ReadString(row, "Identifier"), StringComparer.Ordinal)
                .Where(static group => group.Count() == 1)
                .Select(static group => group.Single())
                .OrderBy(static row => ReadString(row, "Identifier"), StringComparer.Ordinal)
                .Select((row, index) => new HardwareMonitorFanIdentity(index,
                    FanIdentity(provider, ReadString(row, "Identifier")!))).ToArray();
            return new(true, identities);
        }
        return new(false, []);
    }

    private static string FanIdentity(string provider, string identifier) => provider + "|" + identifier;
    public HardwareMonitorSensorSnapshot Read(TimeSpan queryTimeout)
    {
        var libre = QueryHardwareMonitorSensors(@"root\LibreHardwareMonitor", queryTimeout);
        if (libre.Rows.Count > 0 || libre.Error is null)
        {
            return BuildHardwareMonitorSnapshot(
                "LibreHardwareMonitor WMI",
                libre,
                QueryHardwareMonitorHardware(@"root\LibreHardwareMonitor", queryTimeout));
        }

        var open = QueryHardwareMonitorSensors(@"root\OpenHardwareMonitor", queryTimeout);
        if (open.Rows.Count > 0 || open.Error is null)
        {
            return BuildHardwareMonitorSnapshot(
                "OpenHardwareMonitor WMI",
                open,
                QueryHardwareMonitorHardware(@"root\OpenHardwareMonitor", queryTimeout));
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

    private QueryResult QueryHardwareMonitorSensors(string path, TimeSpan timeout)
    {
        return query(path, "SELECT Identifier, Name, Parent, SensorType, Value FROM Sensor", timeout);
    }

    private QueryResult QueryHardwareMonitorHardware(string path, TimeSpan timeout)
    {
        return query(path, "SELECT Identifier, Name, HardwareType FROM Hardware", timeout);
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
        var cpuTemperature = ReadCpuTemperature(temperatureRows, hardwareByIdentifier);
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
            string.Empty,
            row => IsMotherboardTemperatureSensor(row, hardwareByIdentifier),
            "motherboard",
            "mainboard",
            "system",
            "board",
            "temp");
        var vrmTemperature = ReadPreferredMotherboardSensor(
            provider,
            temperatureRows,
            string.Empty,
            row => IsVrmTemperatureSensor(row, hardwareByIdentifier),
            "vrm",
            "mosfet",
            "mos",
            "voltage regulator",
            "vcore");
        var chipsetTemperature = ReadPreferredMotherboardSensor(
            provider,
            temperatureRows,
            string.Empty,
            row => IsChipsetTemperatureSensor(row, hardwareByIdentifier),
            "chipset",
            "pch",
            "southbridge",
            "northbridge");
        var motherboardVoltage = ReadPreferredMotherboardSensor(
            provider,
            result.Rows.Where(static row => IsSensorType(row, "Voltage")).ToArray(),
            string.Empty,
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
            .Where(static row => !string.IsNullOrWhiteSpace(ReadString(row, "Identifier")))
            .GroupBy(static row => ReadString(row, "Identifier"), StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .OrderBy(static row => ReadString(row, "Identifier"), StringComparer.Ordinal)
            .Select((row, index) => new SystemFanSensor(
                index,
                ReadString(row, "Name") ?? string.Empty,
                ParseFanRpm(row.GetValueOrDefault("Value")),
                FindMatchingControlPercent(controlRows, row),
                ReadString(row, "Name") ?? string.Empty)
            {
                IdentityKey = FanIdentity(provider == "LibreHardwareMonitor WMI"
                    ? @"root\LibreHardwareMonitor" : @"root\OpenHardwareMonitor", ReadString(row, "Identifier")!)
            })
            .ToArray();
        var gpuSensors = BuildGpuSensorSnapshots(provider, result.Rows, hardwareByIdentifier);

        var state = cpuTemperature is not null || memoryTemperature is not null
            || motherboardTemperature is { Value: not null }
            || vrmTemperature is { Value: not null }
            || chipsetTemperature is { Value: not null }
            || motherboardVoltage is { Value: not null }
            || cpuFan is not null
            || cpuFanPercent is not null
            || systemFans.Any(static fan => fan.SpeedRpm is not null || fan.SpeedPercent is not null)
            || gpuSensors.Any(static sensor => sensor.HasAnyValue)
                ? new HardwareSensorProviderState(provider, "Active", null)
                : new HardwareSensorProviderState(provider, "Unavailable", null);

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
            gpuSensors,
            cpuTemperature);
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
