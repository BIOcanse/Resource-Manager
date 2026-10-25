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
    // supported SQLite/manual-software registries, report target rows and protection JSON
    // have been rewritten or retired.
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
    public const string Dependency = "dependency";
    public const string Support = "support";

    public static bool IsKnown(string value)
    {
        return value is Dependency or Support;
    }
}

public sealed record SoftwareRecord(
    string Id,
    string Name,
    string Kind,
    string State,
    IReadOnlyList<SoftwareSourceFact> SourceFacts,
    IReadOnlyList<string> RootPaths,
    SoftwareOperationCapabilities Operations,
    string? ManagementRole,
    bool RequiresRootPathConfirmation = false,
    bool IdentityConfirmed = true,
    IReadOnlyList<string>? SuggestedRootPaths = null,
    IReadOnlyList<string>? ExecutablePaths = null,
    string? SoftwareIdentityId = null,
    IReadOnlyList<SoftwareIssueTag>? Issues = null,
    /// <summary>发布者与版本是事实，不走消息码；前端负责拼接与省略。</summary>
    string? Publisher = null,
    string? Version = null,
    /// <summary>Manual or policy classification, which precedes OS/catalog category evidence.</summary>
    bool HasExplicitKind = false,
    string? SourceSoftwareId = null,
    string? CategorySource = null,
    SoftwareRegistrationFacts? RegistrationFacts = null);

public sealed record SoftwareSourceFact(
    string Kind,
    string? CatalogVersion = null,
    string? CatalogSource = null,
    string? PolicyGroupId = null,
    string? PolicyGroupName = null);

public sealed record SoftwareRegistrationFacts(
    string Kind,
    int ProcessCount = 0,
    int ServiceCount = 0,
    string? EndpointState = null);

public static class SoftwareSourceKinds
{
    public const string Dependency = "dependency";
    public const string WindowsRegistry = "windows_registry";
    public const string Self = "self";
    public const string Adapter = "adapter";
    public const string Controlled = "controlled";
    public const string Portable = "portable";
    public const string Manual = "manual";
    public const string IdentityCatalog = "identity_catalog";
    public const string Policy = "policy";
    public const string Runtime = "runtime";
}

public sealed record SoftwareOperationCapabilities(
    bool CanUninstall,
    string UninstallKind,
    string UninstallConstraint);

public static class SoftwareUninstallConstraints
{
    public const string None = "none";
    public const string EmptyManagedRoot = "empty_managed_root";
    public const string MissingUninstallEntry = "missing_uninstall_entry";
    public const string ManualRegistration = "manual_registration";
    public const string MissingStrategy = "missing_strategy";
    public const string Self = "self";
    public const string Portable = "portable";
}

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
    IReadOnlyList<string> RootPaths,
    string? SourceSoftwareId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ManualSoftwareRequest(
    string Name,
    string Kind,
    IReadOnlyList<string> RootPaths,
    string? SourceSoftwareId);
