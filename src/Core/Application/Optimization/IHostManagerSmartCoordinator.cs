using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IHostManagerSmartCoordinator
{
    Task<HostManagerSmartCoordinatorStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<HostManagerRollbackStateDocument> GetStateAsync(CancellationToken cancellationToken);

    Task<HostManagerSmartCoordinatorStatus> SetModeAsync(
        string mode,
        CancellationToken cancellationToken);

    Task<HostManagerSmartCoordinatorStatus> RunOnceAsync(CancellationToken cancellationToken);

    Task<HostManagerSmartCoordinatorStatus> RestoreNormalModeAsync(CancellationToken cancellationToken);
}
