using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Intel;

public sealed partial class IntelGclReader
{
    internal static bool IsPciField(MetricFieldCode code)
        => IsPciStateField(code) || IsPciPropertiesField(code);
    internal static bool IsPciStateField(MetricFieldCode code)
        => code is MetricFieldCode.IgclPciCurrentGeneration or MetricFieldCode.IgclPciCurrentWidth;
    internal static bool IsPciPropertiesField(MetricFieldCode code)
        => code is MetricFieldCode.IgclPciMaximumGeneration or MetricFieldCode.IgclPciMaximumWidth;

    private static IntelGclMetricRead DecodePci(int index, IntelGclPciState state,
        IReadOnlySet<MetricFieldCode> requested, IntelGclMetricRead? telemetry)
    {
        Dictionary<MetricFieldCode, double?> values = telemetry is null ? [] : new(telemetry.Values);
        if (requested.Contains(MetricFieldCode.IgclPciCurrentGeneration))
            values[MetricFieldCode.IgclPciCurrentGeneration] = state.Speed.Generation >= 0 ? state.Speed.Generation : null;
        if (requested.Contains(MetricFieldCode.IgclPciCurrentWidth))
            values[MetricFieldCode.IgclPciCurrentWidth] = state.Speed.Width >= 0 ? state.Speed.Width : null;
        return new(index, values, telemetry?.Counters ?? new Dictionary<MetricFieldCode, ulong?>(),
            telemetry?.Flags ?? new Dictionary<MetricFieldCode, bool?>())
            { FloatingCounters = telemetry?.FloatingCounters ?? new Dictionary<MetricFieldCode, NativeMetricSnapshotFloatingCounterPair?>() };
    }

    private static IntelGclMetricRead DecodePciProperties(int index, IntelGclPciProperties properties,
        IReadOnlySet<MetricFieldCode> requested, IntelGclMetricRead? prior)
    {
        Dictionary<MetricFieldCode, double?> values = prior is null ? [] : new(prior.Values);
        if (requested.Contains(MetricFieldCode.IgclPciMaximumGeneration))
            values[MetricFieldCode.IgclPciMaximumGeneration] = properties.MaximumSpeed.Generation >= 0 ? properties.MaximumSpeed.Generation : null;
        if (requested.Contains(MetricFieldCode.IgclPciMaximumWidth))
            values[MetricFieldCode.IgclPciMaximumWidth] = properties.MaximumSpeed.Width >= 0 ? properties.MaximumSpeed.Width : null;
        return new(index, values, prior?.Counters ?? new Dictionary<MetricFieldCode, ulong?>(),
            prior?.Flags ?? new Dictionary<MetricFieldCode, bool?>())
            { FloatingCounters = prior?.FloatingCounters ?? new Dictionary<MetricFieldCode, NativeMetricSnapshotFloatingCounterPair?>() };
    }
}
