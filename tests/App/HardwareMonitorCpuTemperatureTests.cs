using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMonitorCpuTemperatureTests
{
    private static Dictionary<string, object?> Sensor(string parent, string name, object? value)
        => new() { ["Parent"] = parent, ["Name"] = name, ["SensorType"] = "Temperature", ["Value"] = value };

    [Fact]
    public void CpuPackageWinsOverCoreAndUnrelatedSensorReadings()
    {
        var hardware = new Dictionary<string, HardwareMonitorHardwareInfo>
        {
            ["/intelcpu/0"] = new("/intelcpu/0", "Intel CPU", "Cpu"),
            ["/gpu/0"] = new("/gpu/0", "GPU", "GpuNvidia")
        };
        var rows = new[] { Sensor("/intelcpu/0", "CPU Package", 72d), Sensor("/intelcpu/0", "CPU Core #1", 80d),
            Sensor("/gpu/0", "GPU Core", 95d), Sensor("/lpc/0", "CPU", 100d) };
        Assert.Equal(72, HardwareMonitorWmiReader.ReadCpuTemperature(rows, hardware));
        Assert.Equal(80, HardwareMonitorWmiReader.ReadCpuTemperature(rows.Skip(1).ToArray(), hardware));
        Assert.Null(HardwareMonitorWmiReader.ReadCpuTemperature([Sensor("/intelcpu/0", "CPU Package", double.NaN)], hardware));
    }

    [Fact]
    public void CpuTemperaturePreservesLegacyPriorityWithoutMergingDifferentPhysicalDomains()
    {
        var projection = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var bindings = projection.RuleBindings.Values.Where(rule => rule.LegacyMetricId == "cpu.temperature").ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.Equal(2, bindings.Select(rule => rule.MetricHandle).Distinct().Count());
        Assert.Contains(bindings, rule => rule.SourceId == "amd.smu");
        Assert.Contains(bindings, rule => rule.SourceId == "hardware-monitor.core");
        Assert.Equal(Assert.Single(bindings, rule => rule.SourceId == "amd.smu").MetricHandle,
            projection.LegacyMetricHandles["cpu.temperature"]);
    }
}
