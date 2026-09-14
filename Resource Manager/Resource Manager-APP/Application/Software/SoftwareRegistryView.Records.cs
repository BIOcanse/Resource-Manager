using ResourceManager.App.Domain.Dependencies;
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
            status.Message,
            new SoftwareOperationCapabilities(
                DirectoryHasContent(status.InstallDirectory),
                "ManagedRootCleanup",
                "卸载",
                DirectoryHasContent(status.InstallDirectory)
                    ? "删除该依赖的受管 Dependencies 根目录。"
                    : "该依赖当前没有可清理的受管安装内容。"),
            SoftwareManagementRoles.Dependency);
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
                    NoUninstall("受控软件卸载策略需要独立记录；当前只支持数据迁移恢复。"),
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
            NoUninstall("手动分类/补录项不直接代表卸载器；需要从软件详情或原始软件入口处理。"),
            null,
            SoftwareIdentityId: softwareIdentityId);
    }

    private static SoftwareRecord ToOtherSoftwareRecord(InstalledSoftwareEntry entry)
    {
        var details = new[]
            {
                entry.Publisher,
                entry.Version,
                string.IsNullOrWhiteSpace(entry.UninstallString) ? "缺少卸载入口" : "有卸载入口"
            }
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        return new SoftwareRecord(
            entry.Id,
            entry.Name,
            SoftwareKinds.Other,
            SoftwareText.General,
            "installed",
            ["Windows卸载注册表"],
            entry.RootPaths,
            string.Join(" · ", details),
            string.IsNullOrWhiteSpace(entry.UninstallString)
                ? NoUninstall("缺少卸载入口，只能跳转或手动处理，Resource Manager 不删除未知软件根目录。")
                : new SoftwareOperationCapabilities(
                    true,
                    "WindowsUninstaller",
                    "卸载",
                    "启动 Windows 注册表提供的官方卸载器。"),
            null);
    }
}
