using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 一条真正能写硬件的路径：NVAPI、ADLX、NBFC 各一个。
///
/// **"这一项现在能不能写"只有写入器知道，所以由它回答。**
/// 目录只描述能力的形状（叫什么、是数值还是曲线），能不能用、为什么不能用、
/// 以及真实可调范围，都从这里来 —— 否则两边各存一份，迟早对不上：
/// 目录说能调，写下去却报不支持。
/// </summary>
public interface IControlWriter
{
    /// <summary>
    /// 这个写入器能不能写这一项。
    ///
    /// 只做探测，不写任何东西 —— 目录每次读取都会调它。
    /// </summary>
    ControlWriteAvailability Probe(ControlObject target, ControlCapability capability);

    /// <summary>
    /// 把一项设定写下去。只有 <see cref="Probe"/> 说能写的才会走到这里。
    ///
    /// 传进来的值**已经由转发层换算成这一项 <see cref="ControlNumberRange.Unit"/> 的量纲**，
    /// 后端不必也不应再做单位换算 —— 换算只有一个属主。
    /// </summary>
    Task<ControlApplyOutcome> WriteAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        CancellationToken cancellationToken);

    /// <summary>
    /// 这一项现在实际是多少。
    ///
    /// 实际值和期望值是两回事：固件会按温度自己调度，用户也可能用别的软件改过。
    /// 读不到就回 null —— 读不到和"读到 0"必须能分开。
    ///
    /// 默认读不到：不是每个后端都有回读通道，有的才覆写。
    /// </summary>
    Task<ControlActualValue?> ReadAsync(
        ControlObject target,
        ControlCapability capability,
        CancellationToken cancellationToken)
        => Task.FromResult<ControlActualValue?>(null);
}

/// <summary>
/// 探测结果。
///
/// 写不了就必须给出 <see cref="Reason"/>：界面上那条置灰理由就是这句话，
/// 空着的话用户只能看到一个不能动的控件，分不清是硬件不让还是软件没做。
/// </summary>
public readonly record struct ControlWriteAvailability(
    bool CanWrite,
    string? Reason = null,
    /// <summary>
    /// 硬件实际允许的范围。只有写入器读得到（比如 NVAPI 报的功耗上下限），
    /// 所以由它给；为 null 表示沿用目录里的形状。
    /// </summary>
    ControlNumberRange? Range = null)
{
    public static ControlWriteAvailability Yes(ControlNumberRange? range = null)
        => new(true, null, range);

    public static ControlWriteAvailability No(string reason) => new(false, reason);

    /// <summary>这个写入器根本不负责这一项 —— 和"负责但现在写不了"是两回事。</summary>
    public static ControlWriteAvailability NotMine { get; } = new(false, null);

    public bool IsMine => CanWrite || Reason is not null;
}
