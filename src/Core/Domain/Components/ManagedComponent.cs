using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Domain.Components;

public static class ComponentStatusStates
{
    public const string Active = "active";
    public const string Installed = "installed";
    public const string InstalledUnverified = "installed_unverified";
    public const string ReadyToInstall = "ready_to_install";
    public const string Available = "available";
    public const string ManualDownloadRequired = "manual_download_required";
    public const string ProviderMissing = "provider_missing";
    public const string Missing = "missing";
}

public static class ComponentProviderStates
{
    public const string Active = "active";
    public const string Installed = "installed";
    public const string Missing = "missing";
    public const string RuntimeAvailable = "runtime_available";
    public const string InstalledUnverified = "installed_unverified";
    public const string DriverMissing = "driver_missing";
    public const string RuntimeActive = "runtime_active";
}

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
    BackendMessage Message,
    /// <summary>安装器来源形态，界面据此如实标注按钮并决定要不要问版本。</summary>
    string InstallerSourceKind = DependencyInstallerSourceKinds.Manual);

/// <summary>组件获取动作请求。<c>VersionChoice</c> 为 null 时按已验证版本处理。</summary>
public sealed record ComponentActionRequest(
    bool AcknowledgeExternalTerms,
    string? VersionChoice = null);

public sealed record ComponentActionResult(
    string Id,
    string State,
    BackendMessage Message,
    string? FilePath = null,
    long? BytesWritten = null,
    ComponentStatus? Status = null,
    FileChangeReport? FileChanges = null);
