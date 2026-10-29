namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class RuntimeSpecializationCoordinator
{
    internal async Task SynchronizeSmartRecordHistorySettingAsync(CancellationToken token)
    {
        // The ordinary settings commit boundary also runs while Smart is stopped.
        // Load observes committed settings, enrolls existing receipts and awaits their
        // persistence. Startup already loads this owner in InitializeDecisionFactsAsync.
        if (smartRollbackStateStore is not null
            && (await settingsStore.LoadReadOnlyAsync(token)).Settings.Performance.KeepSmartRecordHistory)
            await smartRollbackStateStore.LoadAsync(token);
    }
}
