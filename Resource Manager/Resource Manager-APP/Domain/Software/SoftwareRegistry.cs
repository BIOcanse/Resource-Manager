using ResourceManager.App.Domain.Messages;
namespace ResourceManager.App.Domain.Software;

public static class SoftwareKinds
{
    public const string Adapted = "Adapted";
    public const string Controlled = "Controlled";
    public const string Managed = "Managed";
    public const string Game = "Game";
    public const string HighPerformance = "HighPerformance";
    public const string Other = "Other";
    public const string WindowsSystem = "WindowsSystem";
    public const string WindowsComponent = "WindowsComponent";
    public const string WindowsService = "WindowsService";
    public const string RuntimePackage = "RuntimePackage";
    public const string RuntimeProduct = "RuntimeProduct";
    public const string RuntimeRoot = "RuntimeRoot";
    public const string Unattributed = "Unattributed";
}

public static class SoftwareManagementRoles
{
    public const string Dependency = "Dependency";
    public const string Support = "Support";

    public static bool IsKnown(string value)
    {
        return value is Dependency or Support;
    }
}

public sealed record SoftwareRecord(
    string Id,
    string Name,
    string Kind,
    string DisplayKind,
    string State,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> RootPaths,
    string Message,
    SoftwareOperationCapabilities Operations,
    string? ManagementRole,
    bool RequiresRootPathConfirmation = false,
    bool IdentityConfirmed = true,
    IReadOnlyList<string>? SuggestedRootPaths = null,
    IReadOnlyList<string>? ExecutablePaths = null,
    string? SoftwareIdentityId = null,
    IReadOnlyList<SoftwareIssueTag>? Issues = null,
    /// <summary>这条记录要说的话。给了码就以码为准，前端按当前语言渲染；还没迁的生产方继续用 Message。</summary>
    BackendMessage? MessageCode = null,
    /// <summary>发布者与版本是事实，不走消息码；前端负责拼接与省略。</summary>
    string? Publisher = null,
    string? Version = null);

public sealed record SoftwareOperationCapabilities(
    bool CanUninstall,
    string UninstallKind,
    string UninstallLabel,
    string UninstallMessage,
    /// <summary>操作名与说明的消息码；给了码就以码为准。</summary>
    BackendMessage? UninstallLabelCode = null,
    BackendMessage? UninstallMessageCode = null);

public sealed record SoftwareOperationRequest(
    string Id,
    bool ConfirmOperation);

public sealed record SoftwareOperationResult(
    string Id,
    string State,
    string Message,
    ResourceManager.App.Domain.Operations.FileChangeReport? FileChanges = null);

public sealed record InstalledSoftwareEntry(
    string Id,
    string Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    IReadOnlyList<string> RootPaths,
    string? UninstallString,
    string RegistryPath);

public sealed record ManualSoftwareRecord(
    string Id,
    string Name,
    string Kind,
    string DisplayKind,
    string State,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> RootPaths,
    string Message,
    string? SourceSoftwareId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ManualSoftwareRequest(
    string Name,
    string Kind,
    IReadOnlyList<string> RootPaths,
    string? SourceSoftwareId);
