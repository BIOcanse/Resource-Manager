using System.Runtime.InteropServices;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class PdhCpuFrequencyMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly PdhCpuPerformanceReader reader = new();
    private readonly CpuMaxBoostFrequency? maxBoost;

    public PdhCpuFrequencyMonitoringZone(CpuMaxBoostFrequency? maxBoost = null)
        : base(MonitoringSourceZoneIds.PdhCpuFrequency)
    {
        this.maxBoost = maxBoost;
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

        /*
         * **两个参考频率，别混。**
         *
         * 标称频率（Windows 报的 MaxMhz，本机 2401）是把
         * `% Processor Performance` 换算成 MHz 的基准 —— 那个计数器本来就是
         * "相对标称频率"的比值，睿频时会超过 100（本机实测 167%）。
         *
         * 加速上限（SMU 报的，本机 5350）才是**百分比的分母**：用户问"跑到多少了"，
         * 那个一百指的是这颗芯片能冲到的最高频率。
         *
         * 先前两件事共用一个数，于是百分比拿标称当分母，显示成 167.77% ——
         * 数没错，但摆在一排 0–100% 的指标里就是在骗人。
         *
         * 读不到加速上限（非 AMD、或者辅助进程没装）就退回标称频率当分母，
         * 这时百分比可能超过 100 —— 但来源串会说清参考的是标称频率，
         * 不是悄悄换个含义。
         */
        var nominal = (int)info.Max(static item => item.MaxMhz);
        var boost = maxBoost?.Megahertz is { } limit && limit > nominal
            ? (int)Math.Round(limit)
            : 0;
        var reference = boost > 0 ? boost : nominal;
        var source = boost > 0 ? "Windows 有效频率 / SMU 加速上限" : "Windows 有效频率";
        var performancePercent = reader.ReadPerformancePercent();

        if (performancePercent is > 0 && nominal > 0)
        {
            var current = (int)Math.Round(nominal * performancePercent.Value / 100d);
            return new CpuFrequency(
                current,
                reference,
                MonitoringMetricSanitizer.NormalizeNonNegative(current * 100d / reference),
                source);
        }

        var fallbackPercent = reference > 0 ? powerCurrent * 100d / reference : 0;
        return new CpuFrequency(
            powerCurrent,
            reference,
            MonitoringMetricSanitizer.NormalizeNonNegative(fallbackPercent),
            boost > 0 ? "Windows power info / SMU 加速上限" : "Windows power info");
    }

    public void Dispose()
    {
        reader.Dispose();
    }
}
