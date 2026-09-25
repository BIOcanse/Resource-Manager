using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.GpuPlacement;

/// <inheritdoc cref="IGpuSchedulingAvailability" />
/// <remarks>
/// 没有第二块 GPU 时，不同步无目标的启动拦截规则。
/// </remarks>
public sealed class GpuSchedulingAvailabilityReader(
    IMetricSampler metricSampler) : IGpuSchedulingAvailability
{
    public async ValueTask<GpuSchedulingAvailability> EvaluateAsync(
        CancellationToken cancellationToken)
    {
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
