using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.CpuTopology;

public interface ICpuCoreResidencyReader
{
    CpuCoreResidencySnapshot? Read();

    IDisposable AcquireSubscription(string subscriptionId, TimeSpan refreshInterval);

    IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
