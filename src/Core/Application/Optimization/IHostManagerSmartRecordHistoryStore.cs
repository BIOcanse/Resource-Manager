using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IHostManagerSmartRecordHistoryStore
{
    Task<HostManagerSmartRecordHistoryPage> ReadHistoryAsync(
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken);

    Task<long> ClearHistoryAsync(CancellationToken cancellationToken);
}
