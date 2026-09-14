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
    IReadOnlyList<SoftwareIssueTag>? Issues = null);

public sealed record SoftwareOperationCapabilities(
    bool CanUninstall,
    string UninstallKind,
    string UninstallLabel,
    string UninstallMessage);

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
