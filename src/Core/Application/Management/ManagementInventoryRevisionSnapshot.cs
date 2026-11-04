namespace ResourceManager.App.Application.Management;

public sealed record ManagementInventoryRevisionSnapshot(
    string SoftwareRevision,
    string ComponentRevision,
    string BrowserRuntimeRevision);
