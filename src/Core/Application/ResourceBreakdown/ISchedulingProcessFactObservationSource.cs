using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface ISchedulingProcessFactObservationSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        SchedulingProcessMetricMask metricMask,
        TimeSpan refreshInterval);

    // Only read beneath owned demand or for explicit cache inspection. One-shot
    // consumers first fetch the required datasets through IResourceBreakdownPushSource.
    // A None metric mask reads only the published inventory and attribution;
    // it neither requires nor requests a performance-metric publication.
    SchedulingProcessFactSnapshot? ReadLatest(
        SchedulingProcessFactRequest request);
}
