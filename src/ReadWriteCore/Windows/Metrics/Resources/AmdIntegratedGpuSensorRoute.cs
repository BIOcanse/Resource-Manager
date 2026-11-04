using ResourceManager.ReadWrite.Windows.Gpu;
namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>
/// 核显的传感路由：**哪一块卡**的频率、温度、电压该去处理器的 SMU 读。
///
/// AMD 核显也可以由 ADLX 报告指标；本类只负责现有 SMU 字段的精确设备绑定，
/// 不排除 ADLX 候选。哪些字段可读由各来源自己的支持与读数事实决定。
///
/// **不按索引判。** GPU0 不一定是核显：插了独显、换了驱动、甚至改个 BIOS 选项，
/// 顺序都可能变。判据是 Windows 自己给的适配器类型（Integrated / Dedicated）
/// 加上厂商 ID —— 那是系统枚举出来的事实，不是我们按型号名猜的。
///
/// **认一次，之后不变。** 显卡是开机就在的，不会跑着跑着从核显变成独显；
/// 每次采样都重新判一遍，只会让同一台机器在不同时刻给出不同的路由。
/// 所以第一次拿到可信的适配器清单就定下来，后面一直用那个答案。
/// </summary>
public sealed class AmdIntegratedGpuSensorRoute
{
    /// <summary>AMD 的 PCI 厂商号。SMU 那条路只对 AMD 的核显成立。</summary>
    private const uint AmdVendorId = 0x1002;

    private readonly object gate = new();
    private bool settled;
    private int? adapterIndex;

    /// <summary>
    /// 核显在适配器清单里的序号；这台机器上没有 AMD 核显就是 null。
    ///
    /// <paramref name="adapters"/> 只在还没定下来的时候看一眼，定下来之后不再理会。
    /// </summary>
    internal int? Resolve(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        lock (gate)
        {
            if (settled)
            {
                return adapterIndex;
            }
            if (adapters.Count == 0)
            {
                // 清单还没起来，这次不下结论 —— 下次再问。
                return null;
            }

            foreach (var adapter in adapters)
            {
                if (adapter.Kind == WindowsGpuAdapterKind.Integrated
                    && adapter.VendorId == AmdVendorId
                    && !adapter.IsSoftware)
                {
                    adapterIndex = adapter.Index;
                    break;
                }
            }
            settled = true;
            return adapterIndex;
        }
    }

    /// <summary>
    /// 这个指标 id 是不是"核显的某个传感量"，是的话对应 PM table 里的哪一项。
    ///
    /// 当前 SMU 映射实现这三项；其他量是否存在不能从本路由推断。
    /// </summary>
    internal static AmdIntegratedGpuSensorKind? SensorKindOf(string metricId, int adapterIndex)
    {
        var prefix = $"gpu.{adapterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
        if (!metricId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return metricId[prefix.Length..] switch
        {
            "graphicsClock" => AmdIntegratedGpuSensorKind.GraphicsClock,
            "temperature" => AmdIntegratedGpuSensorKind.Temperature,
            "coreVoltage" => AmdIntegratedGpuSensorKind.CoreVoltage,
            _ => null
        };
    }
}

/// <summary>核显在 PM table 里能读到的那三个量。</summary>
internal enum AmdIntegratedGpuSensorKind
{
    GraphicsClock,
    Temperature,
    CoreVoltage
}

