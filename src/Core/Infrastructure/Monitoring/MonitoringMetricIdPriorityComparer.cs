namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class MonitoringMetricIdPriorityComparer : IComparer<string>
{
    public static MonitoringMetricIdPriorityComparer Instance { get; } = new();

    private static readonly IReadOnlyDictionary<string, int> CpuMetricOrder = CreateOrder(
        "usage", "frequency", "frequencyPercent", "smuFrequency", "packagePower", "actualPower",
        "averagePower", "stapmPower", "coreVoltage", "packageCurrent", "tdcCurrent", "edcCurrent",
        "temperature", "fanRpm", "fanPercent", "platformPower", "platformVoltage", "igpuFrequency",
        "igpuVoltage", "igpuTemperature");

    private static readonly IReadOnlyDictionary<string, int> GpuMetricOrder = CreateOrder(
        "usage", "graphicsClock", "graphicsClockPercent", "vram", "vramPercent", "memoryClock",
        "power", "boardPower", "powerLimit", "temperature", "hotspotTemperature", "intakeTemperature",
        "fanRpm", "fanPercent", "coreVoltage", "current");

    private MonitoringMetricIdPriorityComparer()
    {
    }

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftKey = CreateKey(left);
        var rightKey = CreateKey(right);
        var result = leftKey.Group.CompareTo(rightKey.Group);
        if (result == 0)
        {
            result = leftKey.DeviceIndex.CompareTo(rightKey.DeviceIndex);
        }

        if (result == 0)
        {
            result = leftKey.Metric.CompareTo(rightKey.Metric);
        }

        return result != 0
            ? result
            : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static MetricOrderKey CreateKey(string metricId)
    {
        if (metricId.StartsWith("cpu.", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricOrderKey(0, 0, ResolveMetricOrder(CpuMetricOrder, metricId[4..]));
        }

        if (metricId.StartsWith("memory.", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricOrderKey(1, 0, ResolveMetricOrder(metricId[7..], "usage", "percent", "temperature"));
        }

        if (metricId.StartsWith("virtualMemory.", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricOrderKey(2, 0, ResolveMetricOrder(metricId[14..], "usage", "percent"));
        }

        if (TryParseIndexedMetric(metricId, "gpu.", out var gpuIndex, out var gpuMetric))
        {
            return new MetricOrderKey(3, gpuIndex, ResolveMetricOrder(GpuMetricOrder, gpuMetric));
        }

        if (metricId.StartsWith("disk.", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricOrderKey(4, ParseOptionalIndex(metricId, "disk."), ResolveMetricOrder(metricId, "disk.io", "disk.read", "disk.write", "disk.total.activePercent", "disk.total.readBytesPerSec", "disk.total.writeBytesPerSec", "disk.total.queueLength"));
        }

        if (metricId.StartsWith("network.", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricOrderKey(5, 0, ResolveMetricOrder(metricId, "network.traffic", "network.receive", "network.send", "network.raw.traffic", "network.raw.receive", "network.raw.send", "network.total.receiveBytesPerSec", "network.total.sendBytesPerSec", "network.total.utilizationPercent"));
        }

        if (metricId.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricOrderKey(6, 0, 0);
        }

        if (TryParseIndexedMetric(metricId, "fan.", out var fanIndex, out var fanMetric))
        {
            return new MetricOrderKey(7, fanIndex, ResolveMetricOrder(fanMetric, "speed", "speedPercent"));
        }

        return new MetricOrderKey(8, 0, 0);
    }

    private static int ParseOptionalIndex(string metricId, string prefix)
    {
        var remainder = metricId[prefix.Length..];
        var separator = remainder.IndexOf('.');
        return separator > 0 && int.TryParse(remainder[..separator], out var index)
            ? index
            : -1;
    }

    private static bool TryParseIndexedMetric(
        string metricId,
        string prefix,
        out int index,
        out string metricName)
    {
        index = 0;
        metricName = string.Empty;
        if (!metricId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = metricId.IndexOf('.', prefix.Length);
        if (separator <= prefix.Length || !int.TryParse(metricId[prefix.Length..separator], out index))
        {
            return false;
        }

        metricName = metricId[(separator + 1)..];
        return metricName.Length > 0;
    }

    private static int ResolveMetricOrder(IReadOnlyDictionary<string, int> order, string metricName)
    {
        return order.GetValueOrDefault(metricName, order.Count);
    }

    private static int ResolveMetricOrder(string metricName, params string[] orderedNames)
    {
        var index = Array.FindIndex(orderedNames, name => name.Equals(metricName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : orderedNames.Length;
    }

    private static IReadOnlyDictionary<string, int> CreateOrder(params string[] names)
    {
        return names
            .Select(static (name, index) => (name, index))
            .ToDictionary(static item => item.name, static item => item.index, StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct MetricOrderKey(int Group, int DeviceIndex, int Metric);
}
