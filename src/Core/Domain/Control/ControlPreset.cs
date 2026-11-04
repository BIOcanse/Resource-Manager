namespace ResourceManager.App.Domain.Control;

/// <summary>
/// 一份存下来的配置：某个**实例**上的一套设定，用户给它起了名字。
///
/// **配置绑定实例。** 一份配置属于某一块卡、某一颗处理器，不是整台机器的快照 ——
/// 所以选中一个实例就能看到为它存过的几套方案（"日常""游戏""安静"），
/// 而不必在一堆混着别的设备的配置里挑。
///
/// 实例身份见 <c>ControlInstanceIdentity</c>：有出厂唯一标识就用它，否则按型号。
/// 于是换一块一模一样的卡上去，原来存的配置照样认得出来、照样合适。
///
/// 存一份配置不会动硬件。点一份配置是把它的内容载入草稿，落到硬件要用户点应用。
/// </summary>
public sealed record ControlPreset(
    /// <summary>稳定标识。改名字不换 id。</summary>
    string Id,
    /// <summary>这份配置属于哪个实例。</summary>
    string ObjectId,
    string Name,
    /// <summary>这个实例上的设定。值自带单位，所以这条记录是自解释的。</summary>
    IReadOnlyList<ControlSetting> Settings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>存下来的全部配置，跨所有实例。界面按实例筛。</summary>
public sealed record ControlPresetCatalog(IReadOnlyList<ControlPreset> Presets)
{
    public static ControlPresetCatalog Empty { get; } = new([]);
}
