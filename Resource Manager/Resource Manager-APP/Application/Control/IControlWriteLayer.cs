using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 统一写入层。**只管写。**
///
/// 它收的是**带稳定单位的值**，自己去认实例、挑对应的驱动/内核/系统接口、
/// 把单位适配成那一侧要的量纲，然后下发。调用方不需要知道这台机器上
/// 这一项最后是走 NVAPI、SMU 还是厂商固件的 ACPI 接口。
///
/// **读不在这里。** 这台机器现在实际是什么样，属于统一订阅源那一侧 ——
/// 两边分开之后，"用户想要什么"和"机器现在是什么样"各有各的属主和各自的节奏，
/// 不会因为共用一个对象而互相牵制。
///
/// 期望状态机也不在这里：**每个功能部分有自己的期望状态机**，
/// 控制面这一份只是其中之一。它们都把内容交给这一层，这一层不关心它们是谁。
/// </summary>
public interface IControlWriteLayer
{
    Task<ControlApplyReport> ReleaseAsync(
        ControlDesiredState removed,
        ControlDesiredState remaining,
        CancellationToken cancellationToken);

    /// <summary>把一份期望状态写到硬件，逐项回报结果。</summary>
    Task<ControlApplyReport> WriteAsync(
        ControlDesiredState desired,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单位适配：把状态机里那个带单位的值，换成驱动那一侧要的量纲。
///
/// 换不了就**如实失败**，不做静默换算 —— 把"瓦"喂给一个只认"档"的接口，
/// 应当报错，而不是照着数字写下去。
/// </summary>
public static class ControlUnitAdapter
{
    /// <summary>
    /// 把 <paramref name="value"/>（单位 <paramref name="from"/>）换算成 <paramref name="to"/>。
    /// 换不了返回 null。
    /// </summary>
    public static double? Convert(double value, string? from, string to)
    {
        if (!ControlUnits.IsKnown(to))
        {
            return null;
        }
        // 没有单位的旧记录按目标单位解释 —— 它是在只有一种单位的年代写下的，
        // 不该因为加了字段就全部作废。
        if (from is null || string.Equals(from, to, StringComparison.Ordinal))
        {
            return value;
        }
        if (!ControlUnits.IsKnown(from))
        {
            return null;
        }

        // 目前没有任何一对单位之间存在有意义的换算：瓦和档、档和 MHz
        // 都不是同一个量纲。有需要的时候在这里逐对加，
        // 而不是给它一个"差不多就行"的通用换算。
        return null;
    }
}
