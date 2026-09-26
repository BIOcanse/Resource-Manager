using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>配置的持久化。只管存取，不管应用。</summary>
public interface IControlPresetStore
{
    Task<ControlPresetCatalog> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ControlPresetCatalog catalog, CancellationToken cancellationToken);
}

/// <summary>
/// 配置这一摊：存、删、读。**配置绑定实例。**
///
/// 一份配置属于某一块卡、某一颗处理器，所以选中一个实例就能看到为它存过的
/// 几套方案，不必在一堆混着别的设备的配置里挑。
///
/// **这里的任何操作都不会动硬件。** 点一份配置是把它的内容载入草稿，
/// 那一步只发生在前端；真要落到硬件，由用户点「应用」，走期望状态机那条路。
/// 所以这一摊里没有"应用某份配置" —— 应用只有一条路，否则就有两处
/// 定义"应用是什么"。
/// </summary>
public interface IControlPresets
{
    Task<ControlPresetCatalog> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 给某个实例存一份配置。
    ///
    /// **同一个实例下同名的覆盖**，不新建 —— 用户在这块卡上再存一次"游戏"
    /// 就是想更新那一份。不同实例下的同名配置互不相干。
    /// </summary>
    Task<ControlPresetCatalog> SaveAsync(
        string objectId,
        string name,
        IReadOnlyList<ControlSetting> settings,
        CancellationToken cancellationToken);

    Task<ControlPresetCatalog> DeleteAsync(string presetId, CancellationToken cancellationToken);
}
