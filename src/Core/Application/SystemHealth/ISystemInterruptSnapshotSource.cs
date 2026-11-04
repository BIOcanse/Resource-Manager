using ResourceManager.App.Domain.SystemHealth;

namespace ResourceManager.App.Application.SystemHealth;

public interface ISystemInterruptSnapshotSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        TimeSpan refreshInterval);

    SystemInterruptSnapshot Read();
}

// A named subscriber owns ordinary sampling demand for exactly the enumeration's lifetime.
public interface ISystemInterruptSnapshotPushSource
{
    IAsyncEnumerable<SystemInterruptSnapshot> SubscribeAsync(string holder, TimeSpan refreshInterval,
        CancellationToken cancellationToken);
}
