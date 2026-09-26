using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IHostManagerRollbackStateStore
{
    Task<HostManagerRollbackStateDocument> LoadAsync(CancellationToken cancellationToken);

    Task<HostManagerRollbackStateDocument> ReserveNativeHostSessionIncarnationAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        HostManagerRollbackStateDocument document,
        CancellationToken cancellationToken);
}
