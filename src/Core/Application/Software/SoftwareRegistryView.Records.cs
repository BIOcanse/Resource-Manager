using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private static SoftwareRecord ToDependencyRecord(OptionalDependencyStatus status)
    {
        var hasContent = DirectoryHasContent(status.InstallDirectory);
        return new SoftwareRecord(
            $"dependency:{status.Definition.Id}", status.Definition.Id, SoftwareKinds.Managed,
            status.State, [new(SoftwareSourceKinds.Dependency)], [status.EffectiveInstallDirectory],
            new SoftwareOperationCapabilities(hasContent, "ManagedRootCleanup",
                hasContent ? SoftwareUninstallConstraints.None : SoftwareUninstallConstraints.EmptyManagedRoot),
            SoftwareManagementRoles.Dependency);
    }

    private static SoftwareRecord ToManualSoftwareRecord(ManualSoftwareRecord record, string? softwareIdentityId)
    {
        return new SoftwareRecord(
            record.Id, record.Name, record.Kind, "manual", [new(SoftwareSourceKinds.Manual)], record.RootPaths,
            NoUninstall(SoftwareUninstallConstraints.ManualRegistration), null,
            SoftwareIdentityId: softwareIdentityId, SourceSoftwareId: record.SourceSoftwareId,
            RegistrationFacts: new(SoftwareSourceKinds.Manual));
    }

    private static SoftwareRecord ToOtherSoftwareRecord(InstalledSoftwareEntry entry)
    {
        var hasUninstallEntry = !string.IsNullOrWhiteSpace(entry.UninstallString);
        return new SoftwareRecord(
            entry.Id, entry.Name, SoftwareKinds.Other, "installed",
            [new(entry.Id.StartsWith("steam-app-", StringComparison.Ordinal)
                ? SoftwareSourceKinds.SteamLibrary : SoftwareSourceKinds.WindowsRegistry)], entry.RootPaths,
            hasUninstallEntry
                ? new SoftwareOperationCapabilities(true, "WindowsUninstaller", SoftwareUninstallConstraints.None)
                : NoUninstall(SoftwareUninstallConstraints.MissingUninstallEntry),
            null, Publisher: entry.Publisher, Version: entry.Version);
    }
}
