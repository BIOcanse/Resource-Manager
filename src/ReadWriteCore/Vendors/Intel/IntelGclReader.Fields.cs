using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Intel;

public sealed partial class IntelGclReader
{
    internal static bool IsSupportedField(MetricFieldCode code) => IsTelemetryField(code) || IsPciField(code);
    internal static bool IsTelemetryField(MetricFieldCode code) => IsRawTelemetryField(code) || IsDerivedTelemetryField(code);
    internal static bool IsRawTelemetryField(MetricFieldCode code) => code is MetricFieldCode.IgclGpuTemperature or MetricFieldCode.IgclMemoryTemperature or MetricFieldCode.IgclGpuEnergy or MetricFieldCode.IgclMemoryEnergy or MetricFieldCode.IgclGpuVoltage or MetricFieldCode.IgclPowerLimited or MetricFieldCode.IgclTemperatureLimited or MetricFieldCode.IgclUtilizationLimited or MetricFieldCode.IgclCurrentLimited or MetricFieldCode.IgclVoltageLimited or MetricFieldCode.IgclSampleTimestamp or MetricFieldCode.IgclGlobalActivity or MetricFieldCode.IgclRenderComputeActivity or MetricFieldCode.IgclMediaActivity or MetricFieldCode.IgclMemoryReadBytes or MetricFieldCode.IgclMemoryWriteBytes or MetricFieldCode.IgclBoardEnergy or MetricFieldCode.IgclMemoryVoltage or MetricFieldCode.GpuGraphicsClock or MetricFieldCode.GpuMemoryClock;

    internal static IntelGclMetricRead Decode(int index, IntelGclPowerTelemetry telemetry, IReadOnlySet<MetricFieldCode> requested)
    {
        var values = new Dictionary<MetricFieldCode, double?>();
        var counters = new Dictionary<MetricFieldCode, ulong?>();
        var flags = new Dictionary<MetricFieldCode, bool?>();
        foreach (var code in requested)
        {
            switch (code)
            {
                case MetricFieldCode.GpuGraphicsClock:
                    values[code] = ReadNumber(telemetry.GraphicsClock, IntelGclUnit.Megahertz); break;
                case MetricFieldCode.GpuMemoryClock:
                    values[code] = ReadNumber(telemetry.MemoryClock, IntelGclUnit.Megahertz); break;
                case MetricFieldCode.IgclGpuTemperature:
                    values[code] = ReadNumber(telemetry.GpuTemperature, IntelGclUnit.Celsius, temperature: true); break;
                case MetricFieldCode.IgclMemoryTemperature:
                    values[code] = ReadNumber(telemetry.MemoryTemperature, IntelGclUnit.Celsius, temperature: true); break;
                case MetricFieldCode.IgclGpuEnergy:
                    values[code] = ReadNumber(telemetry.GpuEnergy, IntelGclUnit.Joules); break;
                case MetricFieldCode.IgclMemoryEnergy:
                    values[code] = ReadNumber(telemetry.MemoryEnergy, IntelGclUnit.Joules); break;
                case MetricFieldCode.IgclGpuVoltage:
                    values[code] = ReadNumber(telemetry.GpuVoltage, IntelGclUnit.Volts); break;
                case MetricFieldCode.IgclPowerLimited:
                    flags[code] = ReadFlag(telemetry.PowerLimited); break;
                case MetricFieldCode.IgclTemperatureLimited:
                    flags[code] = ReadFlag(telemetry.TemperatureLimited); break;
                case MetricFieldCode.IgclUtilizationLimited:
                    flags[code] = ReadFlag(telemetry.UtilizationLimited); break;
                case MetricFieldCode.IgclCurrentLimited:
                    flags[code] = ReadFlag(telemetry.CurrentLimited); break;
                case MetricFieldCode.IgclVoltageLimited:
                    flags[code] = ReadFlag(telemetry.VoltageLimited); break;
                case MetricFieldCode.IgclSampleTimestamp:
                    values[code] = ReadNumber(telemetry.Timestamp, IntelGclUnit.Seconds); break;
                case MetricFieldCode.IgclGlobalActivity:
                    values[code] = ReadNumber(telemetry.GlobalActivity, IntelGclUnit.Seconds); break;
                case MetricFieldCode.IgclRenderComputeActivity:
                    values[code] = ReadNumber(telemetry.RenderComputeActivity, IntelGclUnit.Seconds); break;
                case MetricFieldCode.IgclMediaActivity:
                    values[code] = ReadNumber(telemetry.MediaActivity, IntelGclUnit.Seconds); break;
                case MetricFieldCode.IgclMemoryReadBytes:
                    counters[code] = ReadCounter(telemetry.MemoryReadBytes); break;
                case MetricFieldCode.IgclMemoryWriteBytes:
                    counters[code] = ReadCounter(telemetry.MemoryWriteBytes); break;
                case MetricFieldCode.IgclBoardEnergy:
                    values[code] = ReadNumber(telemetry.BoardEnergy, IntelGclUnit.Joules); break;
                case MetricFieldCode.IgclMemoryVoltage:
                    values[code] = ReadNumber(telemetry.MemoryVoltage, IntelGclUnit.Volts); break;
            }
        }
        return new(index, values, counters, flags) { FloatingCounters = DecodeFloatingCounters(telemetry, requested) };
    }
}
