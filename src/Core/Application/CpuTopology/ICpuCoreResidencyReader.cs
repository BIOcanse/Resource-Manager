using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.CpuTopology;

public interface ICpuCoreResidencyReader
{
    CpuCoreResidencySnapshot? Read();

    CpuCoreResidencyObservation ReadObservation();

    IDisposable AcquireSubscription(string subscriptionId, TimeSpan refreshInterval);

    IAsyncEnumerable<CpuCoreResidencyObservation> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
