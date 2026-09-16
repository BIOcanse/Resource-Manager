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
/// 把一份期望状态施加到硬件，并如实回报每一项的结果。
///
/// 它**不记**用户想要什么（那是存储的事），也不判断该不该施加（那是调用方的事）。
/// 它只做一件事：按这份期望去写，然后说每一项写成了没有。
/// </summary>
public interface IControlPlanExecutor
{
    Task<ControlApplyReport> ApplyAsync(
        ControlDesiredState desired,
        CancellationToken cancellationToken);
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
    /// 把已经存着的期望状态重新施加一遍。
    ///
    /// 启动时、以及设备重新出现时调用 —— 这就是「设定一次就一直维持」的兑现方式。
    /// </summary>
    Task<ControlApplyReport> ReassertAsync(CancellationToken cancellationToken);
}
