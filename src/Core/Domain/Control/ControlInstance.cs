namespace ResourceManager.App.Domain.Control;

/// <summary>
/// 一台设备的登记条目。
///
/// **实例一旦登记就不再消失**，只在"当前在场"和"当前不在场"之间切换。
/// 配置挂在实例上，所以同一块卡拔下来插回去（哪怕换个槽位）还是同一个实例，
/// 原来的设定自动回来。
///
/// 身份按两条规则定，见 <c>ControlInstanceIdentity</c>：有出厂唯一标识就用它，
/// 没有就按型号。**同型号共用一套设定是想要的行为** ——
/// 换一块一模一样的卡上去，原来那套参数照样合适。
/// </summary>
public sealed record ControlInstance(
    /// <summary>实例标识。和在场时的 <see cref="ControlObject.Id"/> 是同一个值。</summary>
    string Id,
    string Kind,
    /// <summary>登记时看到的名字。设备不在场时界面就显示这个。</summary>
    string DisplayName,
    ControlObjectPlatform Platform,
    /// <summary>显卡的接法；非显卡为 null。</summary>
    string? GpuAttachment,
    /// <summary>
    /// 这个身份能不能唯一认出这台设备。
    ///
    /// 按出厂唯一标识定的才算能（显卡的 GPU UUID 是这种）。退到型号的那些不能 ——
    /// 换一块一模一样的上去我们分辨不出来。**如实标出来，不假装能唯一识别**。
    /// </summary>
    bool IdentityIsUnique,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    /// <summary>
    /// 登记时生成的默认配置：各项能力的默认值。
    /// 「恢复默认」就是把它当期望状态施加下去。
    /// </summary>
    IReadOnlyList<ControlSetting> DefaultSettings);

/// <summary>登记表加上"现在谁在场"。</summary>
public sealed record ControlInstanceView(
    ControlInstance Instance,
    /// <summary>这台设备现在插着、认得到。</summary>
    bool IsPresent);

public sealed record ControlInstanceCatalog(
    IReadOnlyList<ControlInstanceView> Instances,
    DateTimeOffset ReadAt);

/// <summary>登记表的持久化形状。只存实例本身，在不在场是现算的。</summary>
public sealed record ControlInstanceRegistryFile(
    IReadOnlyList<ControlInstance> Instances)
{
    public static ControlInstanceRegistryFile Empty { get; } = new([]);
}
