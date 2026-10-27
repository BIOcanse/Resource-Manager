using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

/// <summary>
/// 实际状态的当前值。
///
/// **读取只返回当前值，不触发采样，也不等待采样。** 采样由后台那一条独立的路
/// 在有订阅需求时按自己的节奏做，新结果先完整读出来再原子替换；读不到就写空值。
/// 最后一个订阅退出只停止采样，不清除当前值。
/// 消费端（订阅源、界面）拿到直接显示 —— 不能因为某个订阅者来读就去碰一次硬件，
/// 那样订阅者一多，SMU 邮箱就排队了。
/// </summary>
public interface IControlActualStateOwner
{
    /// <summary>当前值。还没采过就是空的那一份。</summary>
    ControlActualState Current { get; }

    /// <summary>持有期间启用唯一的实际值采样路径。释放最后一个租约后停止采样。</summary>
    IDisposable AcquireSubscription();

    /// <summary>A named one-shot demand, completed by the next new publication.
    /// The retained Current property is reserved for held subscriptions.</summary>
    Task<ControlActualState?> FetchOnceAsync(string holder, CancellationToken cancellationToken)
        => throw new NotSupportedException("This control state owner does not provide subscription single fetch.");
}
