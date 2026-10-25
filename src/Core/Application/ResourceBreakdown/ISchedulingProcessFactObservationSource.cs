using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface ISchedulingProcessFactObservationSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        SchedulingProcessMetricMask metricMask,
        TimeSpan refreshInterval);

    // A None metric mask reads only the published inventory and attribution;
    // it neither requires nor requests a performance-metric publication.
    SchedulingProcessFactSnapshot? ReadLatest(
        SchedulingProcessFactRequest request);
}
