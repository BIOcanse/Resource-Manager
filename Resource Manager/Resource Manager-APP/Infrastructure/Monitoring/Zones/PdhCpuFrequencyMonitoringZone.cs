using System.Runtime.InteropServices;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class PdhCpuFrequencyMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly PdhCpuPerformanceReader reader = new();

    public PdhCpuFrequencyMonitoringZone()
        : base(MonitoringSourceZoneIds.PdhCpuFrequency)
    {
    }

    internal CpuFrequency ReadFrequency()
    {
        if (!CanRead)
        {
            return new CpuFrequency(0, 0, 0, "Frozen");
        }

        var processorCount = Environment.ProcessorCount;
        var info = new ProcessorPowerInformation[processorCount];
        var size = Marshal.SizeOf<ProcessorPowerInformation>() * processorCount;
        var result = NativeMethods.CallNtPowerInformation(
            NativeMethods.ProcessorInformation,
            IntPtr.Zero,
            0,
            info,
            size);

        if (result != 0 || info.Length == 0)
        {
            return new CpuFrequency(0, 0, 0, "Unavailable");
        }

        var powerCurrent = (int)Math.Round(info.Average(static item => item.CurrentMhz));
        var reference = (int)info.Max(static item => item.MaxMhz);
        var performancePercent = reader.ReadPerformancePercent();

        if (performancePercent is > 0 && reference > 0)
        {
            var current = (int)Math.Round(reference * performancePercent.Value / 100d);
            return new CpuFrequency(current, reference, MonitoringMetricSanitizer.NormalizeNonNegative(performancePercent.Value), "Windows 有效频率");
        }

        var fallbackPercent = reference > 0 ? powerCurrent * 100d / reference : 0;
        return new CpuFrequency(powerCurrent, reference, MonitoringMetricSanitizer.NormalizeNonNegative(fallbackPercent), "Windows power info");
    }

    public void Dispose()
    {
        reader.Dispose();
    }
}
