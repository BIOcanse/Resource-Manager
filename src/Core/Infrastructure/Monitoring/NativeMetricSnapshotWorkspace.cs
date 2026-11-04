namespace ResourceManager.App.Infrastructure.Monitoring;

// Host checkpoint cadence policy; Execute retains the original workspace gate for the decision/export.
internal static class NativeMetricSnapshotWorkspaceCheckpointExtensions
{
    internal static bool PersistRecoveryCheckpointIfAdvanced(this NativeMetricSnapshotWorkspace workspace, DateTimeOffset commandAt)
        => workspace.Execute(session => {
            var header = workspace.ReadHeader();
            var catalogAdvanced = header.CatalogGeneration != workspace.PersistedCatalogGeneration;
            if (!catalogAdvanced) return false;
            _ = workspace.ExportAndPersist(commandAt);
            return true;
        });
}
