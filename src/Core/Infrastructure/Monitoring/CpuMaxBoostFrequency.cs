using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>
/// 这颗处理器的**加速频率上限**，单位 MHz。
///
/// Windows 给不出这个数，三个来源报的都是标称频率（本机全是 2401）：
/// <c>Win32_Processor.MaxClockSpeed</c>、PDH 的 <c>Processor Frequency</c>、
/// 注册表 <c>~MHz</c>。PDH 还有个 <c>% of Maximum Frequency</c>，
/// 它比的是电源计划当前允许的上限，也不是加速上限。
///
/// 所以问 SMU（硬件写入辅助进程里那条路，本机读到 5350 MHz）。
/// **只问一次**：加速上限是这颗芯片的固有属性，不随负载变。
/// 问不到就是 null —— 调用方据此如实说参考值是标称频率，不拿标称冒充加速。
/// </summary>
public sealed class CpuMaxBoostFrequency(
    HardwareBridgeClient bridge,
    ILogger<CpuMaxBoostFrequency>? logger = null)
{
    private readonly object gate = new();
    private bool asked;
    private double? megahertz;

    /// <summary>读不到就是 null。</summary>
    public double? Megahertz
    {
        get
        {
            lock (gate)
            {
                if (asked)
                {
                    return megahertz;
                }
                asked = true;
                megahertz = Ask();
                return megahertz;
            }
        }
    }

    private double? Ask()
    {
        if (!bridge.IsInstalled)
        {
            return null;
        }
        try
        {
            var reading = bridge.SendAsync("read", null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (reading is not { } result
                || !result.TryGetProperty("maxBoostFrequencyMhz", out var value)
                || value.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            var reported = value.GetDouble();
            // 超出常识范围的值说明读到的不是频率。
            return reported is > 0 and < 10_000 ? reported : null;
        }
        catch (Exception error) when (error is IOException
            or JsonException
            or InvalidOperationException
            or OperationCanceledException)
        {
            logger?.LogWarning(error, "读不到处理器的加速频率上限。");
            return null;
        }
    }
}
