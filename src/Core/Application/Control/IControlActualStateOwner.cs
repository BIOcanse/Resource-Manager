using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 实际状态的当前值。
///
/// **读取只返回当前值，不触发采样，也不等待采样。** 采样由后台那一条独立的路
/// 按自己的节奏做，新结果先完整读出来再原子替换；读不到就写空值。
/// 消费端（订阅源、界面）拿到直接显示 —— 不能因为某个订阅者来读就去碰一次硬件，
/// 那样订阅者一多，SMU 邮箱就排队了。
/// </summary>
public interface IControlActualStateOwner
{
    /// <summary>当前值。还没采过就是空的那一份。</summary>
    ControlActualState Current { get; }
}
