using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.GpuPlacement;

/// <inheritdoc cref="IGpuSchedulingAvailability" />
public sealed class GpuSchedulingAvailabilityReader(
    IAppSettingsStore settingsStore,
    IMetricSampler metricSampler) : IGpuSchedulingAvailability
{
    public async ValueTask<GpuSchedulingAvailability> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadReadOnlyAsync(cancellationToken);
        var mode = settings.Settings.Performance.GpuSchedulingMode;

        if (string.Equals(mode, AppAdaptiveBooleanModes.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return new GpuSchedulingAvailability(
                false,
                BackendMessage.Create(
                    BackendMessageDomains.GpuPlacement,
                    BackendMessageCodes.GpuPlacement.DisabledBySetting));
        }

        if (string.Equals(mode, AppAdaptiveBooleanModes.Enabled, StringComparison.OrdinalIgnoreCase))
        {
            return new GpuSchedulingAvailability(true, null);
        }

        // 自动：只有不止一个显卡才有可选目标。
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
