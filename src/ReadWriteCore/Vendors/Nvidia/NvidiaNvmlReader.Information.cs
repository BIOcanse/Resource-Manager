using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public sealed partial class NvidiaNvmlReader
{
    internal delegate int UInt32Query(IntPtr device, out uint value);
    internal delegate int EngineUtilizationQuery(IntPtr device, out uint percent, out uint samplingPeriodMicroseconds);

    [ResourceManager.ReadWrite.RuntimeReadTransport]

    internal static NvmlInformationReadings ReadAdditionalMetrics(
        IntPtr device,
        IReadOnlySet<MetricFieldCode> requested,
        UInt32Query? performanceStateQuery = null,
        EngineUtilizationQuery? encoderUtilizationQuery = null,
        EngineUtilizationQuery? decoderUtilizationQuery = null)
    {
        var values = requested.ToDictionary(static id => id, static _ => (double?)null);
        var integers = new Dictionary<MetricFieldCode, ulong>();
        var observedAt = new Dictionary<MetricFieldCode, DateTimeOffset>();
        if (requested.Count == 0) return new(values, integers, observedAt);
        if (requested.Contains(MetricFieldCode.NvmlPerformanceState))
        {
            try
            {
                if ((performanceStateQuery ?? NativeMethods.GetPerformanceState)(device, out var state) == NvmlSuccess
                    && state <= 15)
                    integers[MetricFieldCode.NvmlPerformanceState] = state;
            }
            catch (EntryPointNotFoundException) { }
        }
        var fields = new List<NvmlFieldValue>();
        if (requested.Contains(MetricFieldCode.NvmlPowerInstant)) fields.Add(new() { FieldId = 186 });
        if (requested.Contains(MetricFieldCode.NvmlPowerAverage)) fields.Add(new() { FieldId = 185 });
        if (requested.Contains(MetricFieldCode.GpuMemoryTemperature)) fields.Add(new() { FieldId = 82 });
        if (fields.Count > 0)
        {
            try
            {
                var buffer = fields.ToArray();
                if (NativeMethods.GetFieldValues(device, buffer.Length, buffer) == NvmlSuccess)
                {
                    foreach (var field in buffer)
                    {
                        var name = field.FieldId switch { 186 => MetricFieldCode.NvmlPowerInstant, 185 => MetricFieldCode.NvmlPowerAverage, _ => MetricFieldCode.GpuMemoryTemperature };
                        var value = DecodeField(field);
                        values[name] = value;
                        if (value is not null && DecodeFieldTimestamp(field.TimestampUs) is { } timestamp)
                            observedAt[name] = timestamp;
                    }
                }
            }
            catch (EntryPointNotFoundException) { }
        }
        if (requested.Any(static id => id is MetricFieldCode.NvmlFbReserved or MetricFieldCode.NvmlFbUsed or MetricFieldCode.NvmlFbFree))
        {
            try
            {
                var memory = new NvmlMemoryV2 { Version = (uint)Marshal.SizeOf<NvmlMemoryV2>() | (2U << 24) };
                if (NativeMethods.GetMemoryV2(device, ref memory) == NvmlSuccess)
                {
                    SetBytes(MetricFieldCode.NvmlFbReserved, memory.Reserved);
                    SetBytes(MetricFieldCode.NvmlFbUsed, memory.Used);
                    SetBytes(MetricFieldCode.NvmlFbFree, memory.Free);
                }
            }
            catch (EntryPointNotFoundException) { }
        }
        if (requested.Contains(MetricFieldCode.NvmlClocksEvents) || requested.Contains(MetricFieldCode.NvmlSupportedClocksEvents))
        {
            try
            {
                if (requested.Contains(MetricFieldCode.NvmlClocksEvents)
                    && NativeMethods.GetClockEvents(device, out var current) == NvmlSuccess)
                    integers[MetricFieldCode.NvmlClocksEvents] = current;
                if (requested.Contains(MetricFieldCode.NvmlSupportedClocksEvents)
                    && NativeMethods.GetSupportedClockEvents(device, out var supported) == NvmlSuccess)
                    integers[MetricFieldCode.NvmlSupportedClocksEvents] = supported;
            }
            catch (EntryPointNotFoundException) { }
        }
        Scalar(MetricFieldCode.PcieCurrentGeneration, NativeMethods.GetCurrentPcieGeneration);
        Scalar(MetricFieldCode.PcieCurrentWidth, NativeMethods.GetCurrentPcieWidth);
        Scalar(MetricFieldCode.PcieMaximumGeneration, NativeMethods.GetMaximumPcieGeneration);
        Scalar(MetricFieldCode.PcieMaximumWidth, NativeMethods.GetMaximumPcieWidth);
        Scalar(MetricFieldCode.NvmlEnforcedPowerLimit, NativeMethods.nvmlDeviceGetEnforcedPowerLimit, 1000);
        Scalar(MetricFieldCode.NvmlDefaultPowerLimit, NativeMethods.nvmlDeviceGetPowerManagementDefaultLimit, 1000);
        if (requested.Contains(MetricFieldCode.NvmlMemoryUtilization))
        {
            try
            {
                if (NativeMethods.nvmlDeviceGetUtilizationRates(device, out var usage) == NvmlSuccess)
                    Set(MetricFieldCode.NvmlMemoryUtilization, usage.Memory);
            }
            catch (EntryPointNotFoundException) { }
        }
        if (requested.Contains(MetricFieldCode.NvmlEncoderUtilization) || requested.Contains(MetricFieldCode.NvmlEncoderPeriod))
        {
            try
            {
                uint percent;
                uint period;
                var status = encoderUtilizationQuery is null
                    ? NativeMethods.GetEncoderUtilization(device, out percent, out period)
                    : encoderUtilizationQuery(device, out percent, out period);
                if (status == NvmlSuccess)
                { Set(MetricFieldCode.NvmlEncoderUtilization, percent); Set(MetricFieldCode.NvmlEncoderPeriod, period); }
            }
            catch (EntryPointNotFoundException) { }
        }
        if (requested.Contains(MetricFieldCode.NvmlDecoderUtilization) || requested.Contains(MetricFieldCode.NvmlDecoderPeriod))
        {
            try
            {
                uint percent;
                uint period;
                var status = decoderUtilizationQuery is null
                    ? NativeMethods.GetDecoderUtilization(device, out percent, out period)
                    : decoderUtilizationQuery(device, out percent, out period);
                if (status == NvmlSuccess)
                { Set(MetricFieldCode.NvmlDecoderUtilization, percent); Set(MetricFieldCode.NvmlDecoderPeriod, period); }
            }
            catch (EntryPointNotFoundException) { }
        }
        return new(values, integers, observedAt);

        void Set(MetricFieldCode name, double value) { if (requested.Contains(name)) values[name] = value; }
        void SetBytes(MetricFieldCode name, ulong value)
        {
            if (!requested.Contains(name)) return;
            integers[name] = value;
            values[name] = value;
        }
        void Scalar(MetricFieldCode name, UInt32Query query, double divisor = 1)
        {
            if (!requested.Contains(name)) return;
            try { if (query(device, out var value) == NvmlSuccess) values[name] = value / divisor; }
            catch (EntryPointNotFoundException) { }
        }
    }

    internal static bool IsAdditionalMetric(MetricFieldCode code) => code is
        MetricFieldCode.NvmlPerformanceState or
        MetricFieldCode.NvmlPowerInstant or
        MetricFieldCode.NvmlPowerAverage or
        MetricFieldCode.GpuMemoryTemperature or
        MetricFieldCode.NvmlClocksEvents or
        MetricFieldCode.NvmlSupportedClocksEvents or
        MetricFieldCode.PcieCurrentGeneration or
        MetricFieldCode.PcieCurrentWidth or
        MetricFieldCode.PcieMaximumGeneration or
        MetricFieldCode.PcieMaximumWidth or
        MetricFieldCode.NvmlFbReserved or
        MetricFieldCode.NvmlFbUsed or
        MetricFieldCode.NvmlFbFree or
        MetricFieldCode.NvmlMemoryUtilization or
        MetricFieldCode.NvmlEncoderUtilization or
        MetricFieldCode.NvmlDecoderUtilization or
        MetricFieldCode.NvmlEncoderPeriod or
        MetricFieldCode.NvmlDecoderPeriod or
        MetricFieldCode.NvmlEnforcedPowerLimit or
        MetricFieldCode.NvmlDefaultPowerLimit;

    internal sealed record NvmlInformationReadings(
        IReadOnlyDictionary<MetricFieldCode, double?> Values,
        IReadOnlyDictionary<MetricFieldCode, ulong> Integers,
        IReadOnlyDictionary<MetricFieldCode, DateTimeOffset> ObservedAt);

    internal static DateTimeOffset? DecodeFieldTimestamp(long timestampUs)
        => timestampUs > 0 && timestampUs <= (DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10
            ? DateTimeOffset.UnixEpoch.AddTicks(timestampUs * 10) : null;

    internal static double? DecodeField(NvmlFieldValue field)
    {
        if (field.ReturnCode != NvmlSuccess || DecodeFieldTimestamp(field.TimestampUs) is null) return null;
        double? value = field.ValueType switch
        {
            0 => field.Value.Double,
            1 or 2 => field.Value.UInt32, // unsigned long is 32-bit on Windows
            3 => field.Value.UInt64,
            4 => field.Value.Int64,
            5 => field.Value.Int32,
            6 => field.Value.UInt16,
            _ => null
        };
        return value is { } number && double.IsFinite(number)
            ? field.FieldId switch { 185 or 186 => number / 1000d, 82 => number, _ => null }
            : null;
    }
}
