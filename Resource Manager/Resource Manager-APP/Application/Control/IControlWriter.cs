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
    /// 这一项走哪条链路，见 <see cref="ControlChannels"/>。
    ///
    /// **一个写入器可以横跨几条链路**：NVIDIA 那个就是频率偏移走 NVAPI、
    /// 功耗上限走 NVML。所以按能力问，不是按写入器问。
    ///
    /// **按对象加能力问，不是只按能力问**：同一个能力编号在不同对象上可能走不同链路
    /// （独显的频率偏移走 NVAPI，Intel 核显的同名项走 IGCL）。
    ///
    /// 不认领这一项时返回 null。默认实现返回 null，接进来的写入器逐个补上 ——
    /// 没补的那些界面上就不显示链路，而不是显示一个错的。
    ///
    /// **不碰硬件。** 它要在"档位够不着、连探测都不做"的那条路上也能回答 ——
    /// 用户最想知道链路的，恰恰是那些锁着的项。
    /// </summary>
    string? ChannelOf(ControlObject target, string capabilityId) => null;

    /// <summary>
    /// 这个对象**现在跑的**曲线。不认领、或者这次读不到，就是 null。
    ///
    /// 用户要改曲线，起点必须是机器现在真在跑的那条；给他一条我们编的，
    /// 他改出来的东西和这台机器的实际行为没有关系。
    ///
    /// **单独一条按需的路，不进对象清单也不归 <see cref="Probe"/> 管。**
    /// 读一次曲线可能要几十次固件往返，而对象清单是画一次界面就要问一遍的 ——
    /// 混进去的话，光是打开控制页就要等十几秒。
    ///
    /// 默认读不到：不是每条链路都有曲线，有的才覆写。
    /// </summary>
    Task<IReadOnlyList<ControlCurvePoint>?> ReadCurveAsync(
        ControlObject target,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ControlCurvePoint>?>(null);

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
    /// 把这一项**交还硬件默认**。用户取消勾选时走这里。
    ///
    /// 数值项必须真正写回默认值；时钟锁、风扇接管等状态由对应写入器显式释放。
    ///
    /// <paramref name="stillConfigured"/> 是这个对象上**仍然留着设定的那些能力**。
    /// 有它才能处理"几项共用同一份硬件状态"的情况：显卡时钟锁就是这样 ——
    /// 上限和下限落到驱动上是同一次调用，用户只设了上限时，
    /// 下限那一项会走到这里，**这时候绝不能解锁**，否则刚设的上限当场作废。
    /// 只有两项都没了才该真正解锁。
    ///
    /// 返回 false 表示"试了但没成功"。
    /// 默认写回明确的数值默认值；没有默认值时返回 false，由有状态写入器实现释放。
    /// </summary>
    Task<bool> RestoreAsync(
        ControlObject target,
        ControlCapability capability,
        IReadOnlySet<string> stillConfigured,
        CancellationToken cancellationToken)
        => ControlWriterRestoration.RestoreDefaultAsync(this, target, capability, cancellationToken);

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
    ControlNumberRange? Range = null,
    /// <summary>写不了时属于哪一类，见 <see cref="ControlUnavailableKinds"/>。</summary>
    string? UnavailableKind = null,
    /// <summary>
    /// 写不了，但**当前值读得到**。
    ///
    /// 这不是边角情况：显卡的温度墙、降速阈值、保护关机阈值在消费级卡上全是这样 ——
    /// 驱动如实报 89 / 102 / 105，但一律不接受改动。那几个数对用户仍然有用
    /// （"这张卡 89 度就开始降频"），所以要读出来单独列给他看。
    ///
    /// 能写的项自然也能读，所以 <see cref="Yes"/> 不用单独声明这一条。
    /// </summary>
    bool CanRead = false)
{
    /// <summary>这一项的当前值值不值得去读一次。</summary>
    public bool IsReadable => CanWrite || CanRead;

    public static ControlWriteAvailability Yes(ControlNumberRange? range = null)
        => new(true, null, range);

    /// <summary>
    /// 现在写不了，说明原因**和种类**。
    ///
    /// **读得到范围就一并带上。** 写不了不等于"这一项没有范围"——
    /// NVML 那边常常是范围读得到、写入被驱动拒绝。把已经读到的范围丢掉，
    /// 目录就只能退回自己编的形状，界面上显示的上限就成了假的。
    ///
    /// 种类默认是"这台机器做不到"—— 写入器是真去问过硬件的那个，
    /// 它说不行通常就是硬件或驱动说不行。缺组件、还没接这两种要显式写明，
    /// 它们不是硬件的问题。
    /// </summary>
    public static ControlWriteAvailability No(
        string reason,
        ControlNumberRange? range = null,
        string kind = ControlUnavailableKinds.Platform,
        /// <summary>写不了但读得到时置真，界面会把这个值单独列出来。</summary>
        bool canRead = false)
        => new(false, reason, range, kind, canRead);

    /// <summary>这个写入器根本不负责这一项 —— 和"负责但现在写不了"是两回事。</summary>
    public static ControlWriteAvailability NotMine { get; } = new(false, null);

    public bool IsMine => CanWrite || Reason is not null;
}
