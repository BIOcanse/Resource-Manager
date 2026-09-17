namespace ResourceManager.App.Domain.Control;

/// <summary>
/// 一份存下来的配置：用户给它起了名字的一整套设定。
///
/// **配置和期望状态是两回事。** 期望状态只有一份，是"这台机器现在该保持成什么样"；
/// 配置可以有很多份，是"我攒下来的几套方案"。把一份配置**应用**了，
/// 它的内容才会成为当前的期望状态。存一份配置不会动硬件。
///
/// 里面的值自带单位（见 <see cref="ControlSetting"/>），所以这份记录是自解释的 ——
/// 这正是配置要能保存、切换、将来还要能导出的前提。
/// </summary>
public sealed record ControlPreset(
    /// <summary>稳定标识。改名字不换 id，所以"当前用的是哪一份"不会因为改名而丢。</summary>
    string Id,
    string Name,
    ControlDesiredState Desired,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>存下来的全部配置。</summary>
public sealed record ControlPresetCatalog(IReadOnlyList<ControlPreset> Presets)
{
    public static ControlPresetCatalog Empty { get; } = new([]);
}
