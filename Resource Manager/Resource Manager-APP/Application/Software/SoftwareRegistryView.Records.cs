using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private static SoftwareRecord ToDependencyRecord(OptionalDependencyStatus status)
    {
        return new SoftwareRecord(
            $"dependency:{status.Definition.Id}",
            status.Definition.Name,
            SoftwareKinds.Managed,
            SoftwareText.Managed,
            status.State,
            ["组件依赖"],
            [status.EffectiveInstallDirectory],
            string.Empty,
            new SoftwareOperationCapabilities(
                DirectoryHasContent(status.InstallDirectory),
                "ManagedRootCleanup",
                string.Empty,
                string.Empty,
                BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.UninstallAction),
                BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    DirectoryHasContent(status.InstallDirectory)
                        ? BackendMessageCodes.Dependency.ManagedRootRemovable
                        : BackendMessageCodes.Dependency.ManagedRootEmpty)),
            SoftwareManagementRoles.Dependency,
            MessageCode: status.Message);
    }

    private static IEnumerable<SoftwareRecord> ToControlledMigrationRecords(IReadOnlyList<SoftwareDataMigrationRecord> migrationRecords)
    {
        return migrationRecords
            .Where(static record => record.State != "Restored")
            .GroupBy(static record => record.SoftwareName, StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var records = group.ToArray();
                return new SoftwareRecord(
                    $"managed:migration:{NormalizeId(group.Key)}",
                    group.Key,
                    SoftwareKinds.Controlled,
                    SoftwareText.Controlled,
                    "migrated",
                    ["数据迁移"],
                    records.Select(static record => record.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    $"已有 {records.Length} 条迁移记录。",
                    NoUninstall(BackendMessageCodes.Software.ControlledNoUninstall),
                    null);
            });
    }

    private static SoftwareRecord ToManualSoftwareRecord(
        ManualSoftwareRecord record,
        string? softwareIdentityId)
    {
        return new SoftwareRecord(
            record.Id,
            record.Name,
            record.Kind,
            record.DisplayKind,
            record.State,
            record.Sources,
            record.RootPaths,
            record.Message,
            NoUninstall(BackendMessageCodes.Software.ManualClassificationNoUninstall),
            null,
            SoftwareIdentityId: softwareIdentityId);
    }

    private static SoftwareRecord ToOtherSoftwareRecord(InstalledSoftwareEntry entry)
    {
        var hasUninstallEntry = !string.IsNullOrWhiteSpace(entry.UninstallString);
        return new SoftwareRecord(
            entry.Id,
            entry.Name,
            SoftwareKinds.Other,
            SoftwareText.General,
            "installed",
            ["Windows卸载注册表"],
            entry.RootPaths,
            string.Empty,
            hasUninstallEntry
                ? new SoftwareOperationCapabilities(
                    true,
                    "WindowsUninstaller",
                    string.Empty,
                    string.Empty,
                    BackendMessage.Create(
                        BackendMessageDomains.Software,
                        BackendMessageCodes.Software.UninstallAction),
                    BackendMessage.Create(
                        BackendMessageDomains.Software,
                        BackendMessageCodes.Software.WindowsUninstallerDescription))
                : NoUninstall(BackendMessageCodes.Software.NoUninstallUnknownRoot),
            null,
            MessageCode: BackendMessage.Create(
                BackendMessageDomains.Software,
                hasUninstallEntry
                    ? BackendMessageCodes.Software.HasUninstallEntry
                    : BackendMessageCodes.Software.MissingUninstallEntry),
            Publisher: entry.Publisher,
            Version: entry.Version);
    }
}
