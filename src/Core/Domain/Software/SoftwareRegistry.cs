using ResourceManager.App.Domain.Messages;
namespace ResourceManager.App.Domain.Software;

public static class SoftwareKinds
{
    public const string Adapted = "adapted";
    public const string Controlled = "controlled";
    public const string Managed = "managed";
    public const string Game = "game";
    public const string HighPerformance = "high_performance";
    public const string Other = "other";
    public const string WindowsSystem = "windows_system";
    public const string WindowsComponent = "windows_component";
    public const string WindowsService = "windows_service";
    public const string RuntimePackage = "runtime_package";
    public const string RuntimeProduct = "runtime_product";
    public const string RuntimeRoot = "runtime_root";
    public const string Unattributed = "unattributed";

    public static bool IsKnown(string? value)
        => value is Adapted or Controlled or Managed or Game or HighPerformance or Other
            or WindowsSystem or WindowsComponent or WindowsService or RuntimePackage
            or RuntimeProduct or RuntimeRoot or Unattributed;

    // Compatibility read for pre-lowercase persisted registries only. Delete when all
    // supported stored SQLite/manual-software registries have been rewritten or retired.
    public static string ReadStored(string? value)
    {
        var token = value?.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var kind in new[] { Adapted, Controlled, Managed, Game, HighPerformance, Other,
            WindowsSystem, WindowsComponent, WindowsService, RuntimePackage, RuntimeProduct,
            RuntimeRoot, Unattributed })
        {
            if (string.Equals(token, kind.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        throw new InvalidDataException($"Unsupported stored software kind '{value}'.");
    }
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
    string? Version = null,
    /// <summary>Manual or policy classification, which precedes OS/catalog category evidence.</summary>
    bool HasExplicitKind = false,
    string? SourceSoftwareId = null,
    string? CategorySource = null);

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

public sealed record SoftwareOperationCaller(string WindowsSid, uint WindowsSessionId);

public sealed record SoftwareOperationResult(
    string Id,
    string State,
    BackendMessage Message,
    ResourceManager.App.Domain.Operations.FileChangeReport? FileChanges = null);

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
