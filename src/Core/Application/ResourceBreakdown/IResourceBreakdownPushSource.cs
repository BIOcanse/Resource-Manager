using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IResourceBreakdownPushSource
{
    IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(
        string subscriptionId,
        ResourceBreakdownSampleRequest request,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
