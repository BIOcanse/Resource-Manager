using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 用户现在处在哪一档调节权限。三档的含义见 <see cref="ControlAccessLevels"/>。
///
/// 这是**整个程序一个**的设定，不是每个对象一份：用户要的是"我现在愿意承担到哪一步"，
/// 这句话和是哪块卡、哪颗处理器无关。
///
/// **这和 Intel 核显那个豁免是两件事**（见 <see cref="IControlOverclockConsent"/>）：
/// 那个是厂商 API 的硬性要求 —— 调 Intel 核显的频率偏移，哪怕是负向的，
/// 不先接受豁免它就拒绝调用。这一档是我们自己这道闸，管的是"允不允许碰这一类项"。
/// 两者互不替代。
/// </summary>
public interface IControlAccessLevel
{
    /// <summary>
    /// 现在这一档。默认 <see cref="ControlAccessLevels.Normal"/>。
    ///
    /// 同步读：每次列可控对象都要按它判每一项，而那条路是同步的。
    /// 这个值只有用户在设置里改动时才会变，所以读一次记住就够。
    /// </summary>
    string Current { get; }

    /// <summary>
    /// 换一档。
    /// 往高处换要界面先把这一档意味着什么说清楚、用户确认过。
    /// 认不出来的值一律当成最低那一档。
    /// </summary>
    Task SetAsync(string level, CancellationToken cancellationToken);
}
