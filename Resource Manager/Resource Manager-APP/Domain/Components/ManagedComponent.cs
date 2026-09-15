using ResourceManager.App.Domain.Dependencies;
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
    string Message,
    /// <summary>安装器来源形态，界面据此如实标注按钮并决定要不要问版本。</summary>
    string InstallerSourceKind = DependencyInstallerSourceKinds.Manual);

/// <summary>组件获取动作请求。<c>VersionChoice</c> 为 null 时按已验证版本处理。</summary>
public sealed record ComponentActionRequest(
    bool AcknowledgeExternalTerms,
    string? VersionChoice = null);

public sealed record ComponentActionResult(
    string Id,
    string State,
    string Message,
    string? FilePath = null,
    long? BytesWritten = null,
    ComponentStatus? Status = null,
    FileChangeReport? FileChanges = null);
