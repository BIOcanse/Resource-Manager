using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Platform;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMonitorWmiReaderLibraryIntegrationTests
{
    private static Dictionary<string, object?> Row(string id, string name, string type, double value, string parent = "/lpc/0")
        => new(StringComparer.OrdinalIgnoreCase) { ["Identifier"] = id, ["Name"] = name, ["SensorType"] = type, ["Value"] = value, ["Parent"] = parent };

    [Fact]
    public void ReaderAndSharedLeavesBelongToIndependentLibrary()
    {
        var assembly = typeof(ResourceManager.ReadWrite.ReadWriteCore).Assembly;
        Assert.Equal(assembly, typeof(HardwareMonitorWmiReader).Assembly);
        Assert.Equal(assembly, typeof(PlatformSensorReading).Assembly);
        Assert.Equal(assembly, typeof(DiskTemperatureSensor).Assembly);
        Assert.Equal(assembly, typeof(SystemFanSensor).Assembly);
        Assert.Equal(assembly, typeof(PlatformGpuSensor).Assembly);
    }

    [Fact]
    public void ExplicitReadsAreUncachedAndSuccessfulEmptyLibreDoesNotFallback()
    {
        var calls = new List<(string Path, string Query, TimeSpan Timeout)>();
        var reader = new HardwareMonitorWmiReader((path, query, timeout) =>
        {
            calls.Add((path, query, timeout));
            return new([], null);
        });
        Assert.Empty(calls);
        var timeout = TimeSpan.FromMilliseconds(137);
        var first = reader.Read(timeout);
        Assert.Equal("LibreHardwareMonitor WMI", first.ProviderState.Provider);
        Assert.Equal("Unavailable", first.ProviderState.State);
        Assert.Null(first.ProviderState.Message);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, item => { Assert.Equal(@"root\LibreHardwareMonitor", item.Path); Assert.Equal(timeout, item.Timeout); });
        reader.Read(timeout);
        Assert.Equal(4, calls.Count);
        var topology = reader.ReadFanIdentities(timeout);
        Assert.True(topology.Complete);
        Assert.Empty(topology.Identities);
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public void CoreHostWmiFanIdentitiesCarryTheFanDeviceDescriptor()
    {
        // The Core host reads fan identities through this zone; it must publish the same fan descriptor as the
        // ReadWriteCore default resources instead of the "unknown" placeholder.
        var fans = HardwareMonitorWmiMonitoringZone.ReadFanIdentities(1,
            (_, _) => new([Row("/lpc/nct6798d/fan/1", " System Fan #1 ", "Fan", 1200, "/lpc/nct6798d")], null));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, fans.Status);
        var fan = Assert.Single(fans.Identities);
        Assert.Equal("fan", fan.Device.Kind);
        var address = Assert.IsType<ResourceManager.App.Domain.Metrics.MetricDeviceAddress>(fan.Device.Address);
        Assert.Equal("fan", address.Kind);
        Assert.Equal(@"root\LibreHardwareMonitor", address.FanProvider);
        Assert.Equal("/lpc/nct6798d/fan/1", address.SensorIdentifier);
        Assert.Equal("System Fan #1", fan.Device.DisplayName);
        Assert.Null(fan.NotebookOemRole);
        var failed = HardwareMonitorWmiMonitoringZone.ReadFanIdentities(1, (_, _) => new([], "missing provider"));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failed.Status);
        Assert.Empty(failed.Identities);
    }

    [Fact]
    public void SensorRowsWithErrorKeepLibreButTopologySkipsItsError()
    {
        var calls = new List<string>();
        var reader = new HardwareMonitorWmiReader((path, query, _) =>
        {
            calls.Add(path);
            return query.EndsWith("FROM Hardware", StringComparison.Ordinal) ? new([], "hardware missing")
                : new([Row("/fan/a", "Case fan", "Fan", 1400)], path.Contains("Libre", StringComparison.Ordinal) ? "partial" : null);
        });
        var snapshot = reader.Read(TimeSpan.FromSeconds(2));
        Assert.Equal("LibreHardwareMonitor WMI", snapshot.ProviderState.Provider);
        Assert.Equal(1400, snapshot.CpuFanSpeedRpm);
        Assert.Equal(@"root\LibreHardwareMonitor|/fan/a", Assert.Single(snapshot.SystemFans).IdentityKey);
        Assert.DoesNotContain(@"root\OpenHardwareMonitor", calls);
        var identities = reader.ReadFanIdentities(TimeSpan.FromSeconds(2));
        Assert.Equal(@"root\OpenHardwareMonitor|/fan/a", Assert.Single(identities.Identities).IdentityKey);
    }

    [Fact]
    public void EmptyFailedLibreFallsBackAndBothFailedProvidersKeepErrors()
    {
        var reader = new HardwareMonitorWmiReader((path, _, _) => path.Contains("Libre", StringComparison.Ordinal)
            ? new([], "libre error") : new([], null));
        Assert.Equal("OpenHardwareMonitor WMI", reader.Read(TimeSpan.FromSeconds(2)).ProviderState.Provider);
        var failed = new HardwareMonitorWmiReader((path, _, _) => new([], path));
        Assert.False(failed.ReadFanIdentities(TimeSpan.FromSeconds(2)).Complete);
        Assert.Equal(@"root\LibreHardwareMonitor; root\OpenHardwareMonitor", failed.Read(TimeSpan.FromSeconds(2)).ProviderState.Message);
    }

    [Fact]
    public void FanIdentityIsOrdinalUniqueSortedAndMatchesTopology()
    {
        var rows = new[] { Row("z", "Rear", "Fan", 20000), Row("a", "Front", "Fan", 1000), Row("A", "Upper", "Fan", 1500),
            Row("dup", "One", "Fan", 900), Row("dup", "Two", "Fan", 1100), Row("", "Missing", "Fan", 800),
            Row("cpu", "CPU fan", "Fan", 2000), Row("gpu", "GPU fan", "Fan", 3000),
            Row("control", "Front", "Control", 0) };
        var reader = new HardwareMonitorWmiReader((_, query, _) => query.EndsWith("FROM Hardware", StringComparison.Ordinal) ? new([], null) : new(rows, null));
        var snapshot = reader.Read(TimeSpan.FromSeconds(2));
        var topology = reader.ReadFanIdentities(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { @"root\LibreHardwareMonitor|A", @"root\LibreHardwareMonitor|a", @"root\LibreHardwareMonitor|z" }, snapshot.SystemFans.Select(fan => fan.IdentityKey));
        Assert.Equal(new[] { 0, 1, 2 }, snapshot.SystemFans.Select(fan => fan.Index));
        Assert.Equal(snapshot.SystemFans.Select(fan => fan.IdentityKey), topology.Identities.Select(fan => fan.IdentityKey));
        Assert.Equal(0, snapshot.SystemFans[1].SpeedPercent);
        Assert.Null(snapshot.SystemFans[2].SpeedRpm);
    }

    [Fact]
    public void CoreProjectsOriginalRawNamesDetailsAndMessages()
    {
        var rows = new[] { Row("/board/temp", "Provider / Board", "Temperature", 40), Row("/fan/0", "", "Fan", 1400),
            Row("/gpu/temp", "GPU Core", "Temperature", 70, "/gpu/0") };
        var reader = new HardwareMonitorWmiReader((_, query, _) => query.EndsWith("FROM Hardware", StringComparison.Ordinal) ? new([], "no hardware") : new(rows, null));
        var raw = reader.Read(TimeSpan.FromSeconds(2));
        Assert.Equal("Provider / Board", raw.MotherboardTemperature!.Name);
        Assert.Equal("Provider / Board", raw.MotherboardTemperature.Detail);
        Assert.Null(raw.ProviderState.Message);
        Assert.Equal(1400, raw.CpuFanSpeedRpm);
        var formatted = HardwareMonitorWmiMonitoringZone.FormatSnapshot(raw);
        Assert.Equal(MetricDisplayDetailFormatter.SensorDetail("Provider / Board", "主板温度"), formatted.MotherboardTemperature!.Name);
        Assert.Equal(MetricDisplayDetailFormatter.SensorDetail("Provider / Board", "硬件传感器"), formatted.MotherboardTemperature.Detail);
        Assert.Equal(MetricDisplayDetailFormatter.FanDetail("", 0), Assert.Single(formatted.SystemFans).Name);
        Assert.Equal(MetricDisplayDetailFormatter.SensorDetail("", "硬件传感器"), Assert.Single(formatted.SystemFans).Detail);
        var rawGpu = Assert.Single(raw.GpuSensors);
        Assert.Equal(rawGpu.Name, Assert.Single(formatted.GpuSensors).Name);
        Assert.Equal(MetricDisplayDetailFormatter.SensorDetail(rawGpu.Name, "GPU sensor 0"), Assert.Single(formatted.GpuSensors).Detail);
        Assert.Equal("已读取内存温度、主板传感、风扇或 GPU 后备传感器。", formatted.ProviderState.Message);
    }
}
