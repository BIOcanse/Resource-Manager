using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    // These authorities outlive a coordinator run. Decisions can therefore see
    // persisted recovery work before the coordinator is first allowed to run.
    internal StateRead<bool> SmartOwnedStatePendingPublication
    {
        get
        {
            var rollback = (stateStore as JsonHostManagerRollbackStateStore)?.OwnedStatePendingPublication;
            var native = nativeActionTransactions.OwnedStatePendingPublication;
            if (rollback == true || native == true) return StateRead<bool>.FromValue(true);
            return rollback.HasValue && native.HasValue ? StateRead<bool>.FromValue(false)
                : StateRead<bool>.NoValue(NoValueReason.NotPublished);
        }
    }
    internal bool SmartOwnedStatePending => SmartOwnedStatePendingPublication.GetValueOrDefault(false);

    internal event Action? SmartOwnedStateChanged
    {
        add
        {
            if (stateStore is JsonHostManagerRollbackStateStore rollback)
                rollback.OwnedStateChanged += value;
            nativeActionTransactions.OwnedStateChanged += value;
        }
        remove
        {
            if (stateStore is JsonHostManagerRollbackStateStore rollback)
                rollback.OwnedStateChanged -= value;
            nativeActionTransactions.OwnedStateChanged -= value;
        }
    }

    internal async Task InitializeDecisionFactsAsync(CancellationToken cancellationToken)
    {
        // Initialization belongs to the host's serial preparation, never a fact
        // getter or the Smart worker. It opens only the existing durable stores.
        if (stateStore is not JsonHostManagerRollbackStateStore)
            throw new InvalidOperationException("Smart decision facts require the rollback authority's publication.");
        await stateStore.LoadAsync(cancellationToken);
        await nativeActionTransactions.EnsureReadyAsync(cancellationToken);
    }
}
