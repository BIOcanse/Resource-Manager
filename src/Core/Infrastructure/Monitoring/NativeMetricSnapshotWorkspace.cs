using ResourceManager.ReadWrite.Native;
namespace ResourceManager.App.Infrastructure.Monitoring;

// Host checkpoint cadence policy; Execute retains the original workspace gate for the decision/export.
internal static class NativeMetricSnapshotWorkspaceCheckpointExtensions
{
    internal static bool PersistRecoveryCheckpointIfAdvanced(this NativeMetricSnapshotWorkspace workspace, DateTimeOffset commandAt)
        => workspace.Execute(session => {
            var header = workspace.ReadHeader();
            var catalogAdvanced = header.CatalogGeneration != workspace.PersistedCatalogGeneration;
            var firstReadyFrame = header.Phase == (uint)NativeMetricSnapshotPhase.Ready
                && workspace.PersistedPhase != (uint)NativeMetricSnapshotPhase.Ready;
            if (!catalogAdvanced && !firstReadyFrame) return false;
            _ = workspace.ExportAndPersist(commandAt);
            return true;
        });
}
