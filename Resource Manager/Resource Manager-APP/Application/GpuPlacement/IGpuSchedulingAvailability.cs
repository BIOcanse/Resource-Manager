using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.GpuPlacement;

/// <summary>
/// 「这台机器现在要不要跑 GPU 调度」的唯一所有者。
/// 设置里的 <c>performance.gpuSchedulingMode</c> 是用户口径：始终开启 / 始终关闭 / 自动；
/// 自动时由硬件决定——只有一个显卡就没有可选目标，整条链路停用。
/// </summary>
public interface IGpuSchedulingAvailability
{
    ValueTask<GpuSchedulingAvailability> EvaluateAsync(CancellationToken cancellationToken);
}

/// <param name="Enabled">要不要跑。</param>
/// <param name="Reason">不跑的原因；跑的时候为 null。措辞由前端按当前语言决定。</param>
public sealed record GpuSchedulingAvailability(bool Enabled, BackendMessage? Reason);
