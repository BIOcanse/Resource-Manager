using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface ISchedulingProcessFactObservationSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        SchedulingProcessMetricMask metricMask,
        TimeSpan refreshInterval);

    SchedulingProcessFactSnapshot? ReadLatest(
        SchedulingProcessFactRequest request);
}
