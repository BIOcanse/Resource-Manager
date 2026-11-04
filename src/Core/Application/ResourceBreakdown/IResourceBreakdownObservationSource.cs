using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IResourceBreakdownObservationSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        ResourceBreakdownSampleRequest request,
        TimeSpan refreshInterval);

    ResourceBreakdownSnapshot ReadLatest(
        ResourceBreakdownSampleRequest request);
}
