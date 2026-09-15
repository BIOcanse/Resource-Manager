using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.GpuPlacement;

/// <inheritdoc cref="IGpuSchedulingAvailability" />
/// <remarks>
/// 「自动调度性能优化」关掉时，这里永远回答「跑」——用户的设置照常执行，不做任何按硬件的裁剪。
/// 开着时才允许按本机事实优化：只有一个显卡时没有可选目标，整条 GPU 调度链路停用。
/// </remarks>
public sealed class GpuSchedulingAvailabilityReader(
    IAppSettingsStore settingsStore,
    IMetricSampler metricSampler) : IGpuSchedulingAvailability
{
    public async ValueTask<GpuSchedulingAvailability> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadReadOnlyAsync(cancellationToken);
        if (!settings.Settings.Performance.AutomaticSchedulingOptimizationsEnabled)
        {
            return new GpuSchedulingAvailability(true, null);
        }

        var snapshot = await metricSampler.GetSnapshotAsync(
            MetricSampleRequest.CatalogProbe,
            cancellationToken);
        return snapshot.Gpus.Count > 1
            ? new GpuSchedulingAvailability(true, null)
            : new GpuSchedulingAvailability(
                false,
                BackendMessage.Create(
                    BackendMessageDomains.GpuPlacement,
                    BackendMessageCodes.GpuPlacement.SingleAdapter));
    }
}
