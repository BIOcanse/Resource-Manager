using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.GpuPlacement;

/// <summary>
/// 根据当前 GPU 拓扑决定是否同步启动拦截规则；不控制运行时调度模式。
/// </summary>
public interface IGpuSchedulingAvailability
{
    ValueTask<GpuSchedulingAvailability> EvaluateAsync(CancellationToken cancellationToken);
}

/// <param name="Enabled">是否同步启动拦截规则。</param>
/// <param name="Reason">跳过同步的原因；同步时为 null。</param>
public sealed record GpuSchedulingAvailability(bool Enabled, BackendMessage? Reason);
