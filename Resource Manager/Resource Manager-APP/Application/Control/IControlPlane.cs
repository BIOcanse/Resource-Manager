using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 期望状态的唯一所有者。持久化，进程重启之后还在。
/// </summary>
public interface IControlDesiredStateStore
{
    Task<ControlDesiredState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ControlDesiredState desired, CancellationToken cancellationToken);
}

/// <summary>
/// 控制面对外的那一层：读当前状态、改设定、重新施加。
///
/// 「改设定」一定是**先存后施加**：先把用户的意图落到持久化存储，再去写硬件。
/// 反过来的话，写成功但存失败会让下次启动悄悄回到旧值 —— 用户设过的东西不该自己变回去。
/// </summary>
public interface IControlPlane
{
    Task<ControlStateView> ReadStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 替换某个对象上的设定并立刻施加。
    /// 传空的 settings 就是把这个对象的设定清掉（回到不管它的状态）。
    /// </summary>
    Task<ControlStateView> SetObjectSettingsAsync(
        string objectId,
        IReadOnlyList<ControlSetting> settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// 整份替换期望状态并立刻施加。
    ///
    /// 「草稿应用」和「配置应用」走的是同一件事 —— 用户面对的是一整套设定，
    /// 不是一条条分别提交。**没出现在这一份里的项会被恢复到硬件默认**：
    /// 只是"以后不再写它"不够，上次写进去的值还留在硬件里。
    /// </summary>
    Task<ControlStateView> ApplyDesiredStateAsync(
        ControlDesiredState desired,
        CancellationToken cancellationToken);

    /// <summary>
    /// 把已经存着的期望状态重新施加一遍。
    ///
    /// 启动时、以及设备重新出现时调用 —— 这就是「设定一次就一直维持」的兑现方式。
    /// </summary>
    Task<ControlApplyReport> ReassertAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 见过的设备登记表。
///
/// 它和 <see cref="IControlObjectCatalog"/> 的区别是**时间**：
/// 目录回答"现在插着什么"，登记表回答"这台机器上出现过什么"。
/// 配置挂在登记表上，所以拔掉一块卡不会让它的设定变成孤儿。
/// </summary>
public interface IControlInstanceRegistry
{
    /// <summary>
    /// 全部登记过的实例，并标出现在哪些在场。
    /// 顺带把新认到的设备登记进去 —— 插上就该能设，不用先手动刷新。
    /// </summary>
    Task<ControlInstanceCatalog> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 重新检测一遍。新设备会被登记并自带默认配置。
    /// 界面上那个「刷新」就是它。
    /// </summary>
    Task<ControlInstanceCatalog> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 删掉一个实例及其设定。用于清理早就不用的卡。
    /// 设备还在场时不允许删 —— 删了下一次读取又会把它登记回来，等于什么都没发生。
    /// </summary>
    Task<ControlInstanceCatalog> ForgetAsync(string instanceId, CancellationToken cancellationToken);
}
