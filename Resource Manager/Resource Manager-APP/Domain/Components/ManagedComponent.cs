using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Domain.Components;

public sealed record ComponentDefinition(
    string Id,
    string Name,
    string Vendor,
    string Category,
    string Purpose,
    string SourcePageUrl,
    string? ExternalTermsUrl,
    string ManagementRole,
    bool IsBundled,
    bool RequiresExternalTermsAcknowledgement,
    bool RequiresElevation,
    IReadOnlyList<ComponentCapability> Capabilities,
    string InstallNote);

public sealed record ComponentCapability(
    string Id,
    string Label,
    string ProviderId,
    string ProviderKind);

public sealed record ComponentProviderStatus(
    string ProviderId,
    string Name,
    string Vendor,
    string ProviderKind,
    string State,
    string Message,
    IReadOnlyList<string> Capabilities);

public sealed record ComponentStatus(
    ComponentDefinition Definition,
    string State,
    string StateLabel,
    string InstallRoot,
    string InstallerDirectory,
    string? InstallerPath,
    bool InstallerAvailable,
    bool Installed,
    bool ProviderActive,
    bool CanDownload,
    bool CanInstall,
    bool CanVerify,
    IReadOnlyList<ComponentProviderStatus> Providers,
    string Message);

public sealed record ComponentActionRequest(bool AcknowledgeExternalTerms);

public sealed record ComponentActionResult(
    string Id,
    string State,
    string Message,
    string? FilePath = null,
    long? BytesWritten = null,
    ComponentStatus? Status = null,
    FileChangeReport? FileChanges = null);
