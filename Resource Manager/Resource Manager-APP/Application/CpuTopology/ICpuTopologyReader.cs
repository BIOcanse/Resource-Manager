using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.CpuTopology;

public interface ICpuTopologyReader
{
    CpuTopologySnapshot? GetSnapshot();

    IAsyncEnumerable<CpuTopologySnapshot?> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
