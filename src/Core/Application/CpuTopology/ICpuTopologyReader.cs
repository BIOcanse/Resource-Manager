using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Application.CpuTopology;

public interface ICpuTopologyReader
{
    CpuTopologySnapshot? GetSnapshot();

    DeviceInformationSnapshot GetInformationSnapshot();

    IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);

    IAsyncEnumerable<CpuTopologySnapshot?> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
