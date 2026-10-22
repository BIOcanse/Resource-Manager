using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class HardwareMonitorWmiMonitoringZone
{
    internal static NativeMetricSnapshotOrdinalTopologyRead ReadFanIdentities(ulong generation,
        Func<string, string, QueryResult>? query = null)
    {
        var reader = query is null ? new HardwareMonitorWmiReader() : new HardwareMonitorWmiReader((path, statement, _) =>
        {
            var queried = query(path, statement);
            return new(queried.Rows, queried.Error);
        });
        // Shared WMI fan catalog projection, so these identities carry the same fan device descriptor
        // as the ReadWriteCore default resources. This path appended no OEM fan identities before either.
        return WindowsNotebookFanProjection.CreateFanCatalog(reader.ReadFanIdentities(TimeSpan.FromSeconds(2)),
            enableMechrevo: false, generation);
    }

    internal HardwareMonitorSensorSnapshot ReadSensors()
        => CanRead ? FormatSnapshot(new HardwareMonitorWmiReader().Read(TimeSpan.FromSeconds(2))) : HardwareMonitorSensorsFrozen();

    internal static HardwareMonitorSensorSnapshot FormatSnapshot(HardwareMonitorSensorSnapshot raw)
    {
        var state = raw.ProviderState;
        var message = state.Message ?? (state.State == "Active"
            ? "已读取内存温度、主板传感、风扇或 GPU 后备传感器。"
            : "未发现内存温度、主板传感、风扇或 GPU 后备传感器。");
        return raw with
        {
            ProviderState = state with { Message = message },
            MotherboardTemperature = FormatReading(raw.MotherboardTemperature, "主板温度"),
            VrmTemperature = FormatReading(raw.VrmTemperature, "供电温度"),
            ChipsetTemperature = FormatReading(raw.ChipsetTemperature, "芯片组温度"),
            MotherboardVoltage = FormatReading(raw.MotherboardVoltage, "主板电压"),
            SystemFans = raw.SystemFans.Select(fan => fan with
            {
                Name = MetricDisplayDetailFormatter.FanDetail(fan.Name, fan.Index),
                Detail = MetricDisplayDetailFormatter.SensorDetail(fan.Detail, "硬件传感器")
            }).ToArray(),
            GpuSensors = raw.GpuSensors.Select(sensor => sensor with
            {
                Name = string.IsNullOrEmpty(sensor.Name) ? $"GPU sensor {sensor.Index}" : sensor.Name,
                Detail = MetricDisplayDetailFormatter.SensorDetail(sensor.Detail, $"GPU sensor {sensor.Index}")
            }).ToArray()
        };
    }

    private static PlatformSensorReading? FormatReading(PlatformSensorReading? raw, string fallback)
        => raw is null ? null : raw with
        {
            Name = MetricDisplayDetailFormatter.SensorDetail(raw.Name, fallback),
            Detail = MetricDisplayDetailFormatter.SensorDetail(raw.Detail, "硬件传感器")
        };

    private static HardwareMonitorSensorSnapshot HardwareMonitorSensorsFrozen()
        => new(new HardwareSensorProviderState("Hardware monitor WMI", "Frozen",
            "HardwareMonitor WMI 监控源当前处于功能区冻结。"), null, null, null, null, null, null, null, [], []);
}
