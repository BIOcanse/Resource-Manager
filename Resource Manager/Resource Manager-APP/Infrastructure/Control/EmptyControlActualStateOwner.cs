using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 没有采样那条路时的当前值：空的。
///
/// 只读服务图里不该有会碰驱动、会拉起辅助进程的后台服务，所以那张图里没有采样器。
/// 订阅端照样能问，只是问到的是"还没采过" —— **空值和"读到 0"是两回事**，
/// 前者说明这条路没开，后者是硬件真的报了 0。
/// </summary>
public sealed class EmptyControlActualStateOwner : IControlActualStateOwner
{
    public ControlActualState Current => ControlActualState.Empty;
}
