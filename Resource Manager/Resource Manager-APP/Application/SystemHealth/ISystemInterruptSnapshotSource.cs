using ResourceManager.App.Domain.SystemHealth;

namespace ResourceManager.App.Application.SystemHealth;

public interface ISystemInterruptSnapshotSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        TimeSpan refreshInterval);

    SystemInterruptSnapshot Read();
}
