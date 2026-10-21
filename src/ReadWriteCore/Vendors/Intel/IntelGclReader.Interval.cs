using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Intel;

public sealed partial class IntelGclReader
{
    internal static bool IsDerivedTelemetryField(MetricFieldCode code) => code is
        MetricFieldCode.IgclGpuIntervalPower or MetricFieldCode.IgclMemoryIntervalPower or MetricFieldCode.IgclBoardIntervalPower
        or MetricFieldCode.IgclAnyEngineBusy or MetricFieldCode.IgclRenderComputeBusy or MetricFieldCode.IgclAnyMediaEngineBusy;

    private static IReadOnlyDictionary<MetricFieldCode, NativeMetricSnapshotFloatingCounterPair?> DecodeFloatingCounters(
        IntelGclPowerTelemetry telemetry, IReadOnlySet<MetricFieldCode> requested)
    {
        var result = new Dictionary<MetricFieldCode, NativeMetricSnapshotFloatingCounterPair?>();
        var reference = ReadNumber(telemetry.Timestamp, IntelGclUnit.Seconds);
        foreach (var code in requested.Where(IsDerivedTelemetryField))
        {
            var primary = code switch
            {
                MetricFieldCode.IgclGpuIntervalPower => ReadNumber(telemetry.GpuEnergy, IntelGclUnit.Joules),
                MetricFieldCode.IgclMemoryIntervalPower => ReadNumber(telemetry.MemoryEnergy, IntelGclUnit.Joules),
                MetricFieldCode.IgclBoardIntervalPower => ReadNumber(telemetry.BoardEnergy, IntelGclUnit.Joules),
                MetricFieldCode.IgclAnyEngineBusy => ReadNumber(telemetry.GlobalActivity, IntelGclUnit.Seconds),
                MetricFieldCode.IgclRenderComputeBusy => ReadNumber(telemetry.RenderComputeActivity, IntelGclUnit.Seconds),
                MetricFieldCode.IgclAnyMediaEngineBusy => ReadNumber(telemetry.MediaActivity, IntelGclUnit.Seconds),
                _ => null
            };
            result[code] = primary is { } numerator && reference is { } denominator
                ? new(numerator, denominator) : null;
        }
        return result;
    }
}
