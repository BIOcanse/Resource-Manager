using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private Task<HostManagerRollbackStateDocument> LoadRollbackStateAsync(CancellationToken cancellationToken)
    {
        RequireNoGpuWindowOwnerWork();
        return stateStore.LoadAsync(cancellationToken);
    }

    private Task SaveRollbackStateAsync(HostManagerRollbackStateDocument state, CancellationToken cancellationToken)
    {
        RequireNoGpuWindowOwnerWork();
        return stateStore.SaveAsync(state, cancellationToken);
    }

    private Task<HostManagerRollbackStateDocument> ReserveRollbackSessionAsync(CancellationToken cancellationToken)
    {
        RequireNoGpuWindowOwnerWork();
        return stateStore.ReserveNativeHostSessionIncarnationAsync(cancellationToken);
    }
}
