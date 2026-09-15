using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Domain.Dependencies;

/// <summary>
/// 安装器来源的三种形态。界面据此如实标注按钮，后端据此决定下载前要不要先解析。
/// </summary>
public static class DependencyInstallerSourceKinds
{
    /// <summary>目录里写死的下载地址。</summary>
    public const string Direct = "direct";

    /// <summary>运行时解析某个 GitHub 仓库的 latest release 资产。</summary>
    public const string GitHubRelease = "githubRelease";

    /// <summary>没有可自动获取的来源，只能引导用户手动把安装器放进缓存目录。</summary>
    public const string Manual = "manual";
}

/// <summary>
/// 从某个 GitHub 仓库取安装器。提供两个可选版本：
/// <para><b>已验证版本</b>：我们实际验证过能用的 release，由 <see cref="VerifiedTag"/> 与
/// <see cref="VerifiedAssetName"/> 固定。它的下载地址是确定性拼出来的，不需要调用 API，
/// 所以断网或被速率限制时仍然可以安装。</para>
/// <para><b>最新版本</b>：运行时解析该仓库的 latest release，按 <see cref="AssetPatterns"/>
/// 选出资产。解析失败只影响这一个选项，不影响已验证版本。</para>
/// </summary>
public sealed record GitHubReleaseSource(
    string Owner,
    string Repository,
    string VerifiedTag,
    string VerifiedAssetName,
    IReadOnlyList<string> AssetPatterns);

/// <summary>用户在版本对话框里选的是哪一个。</summary>
public static class DependencyVersionChoices
{
    /// <summary>我们验证过能用的固定版本。</summary>
    public const string Verified = "verified";

    /// <summary>上游当前的最新发布。</summary>
    public const string Latest = "latest";
}

public sealed record OptionalDependencyDefinition(
    string Id,
    string Name,
    string Vendor,
    string Category,
    string SourcePageUrl,
    string? DownloadUrl,
    string? ExternalTermsUrl,
    string InstallerFileName,
    IReadOnlyList<string> InstallerFilePatterns,
    string InstallDirectoryName,
    bool RequiresExternalTermsAcknowledgement,
    bool RequiresElevation,
    IReadOnlyList<string> InstalledProbeRelativePaths,
    string InstallNote,
    GitHubReleaseSource? ReleaseSource = null)
{
    /// <summary>这个依赖的安装器来源形态；目录声明什么就是什么，不做推断以外的猜测。</summary>
    public string InstallerSourceKind =>
        !string.IsNullOrWhiteSpace(DownloadUrl)
            ? DependencyInstallerSourceKinds.Direct
            : ReleaseSource is not null
                ? DependencyInstallerSourceKinds.GitHubRelease
                : DependencyInstallerSourceKinds.Manual;
}

/// <summary>解析出的一次可用安装器地址。</summary>
public sealed record ResolvedInstallerSource(
    string DownloadUrl,
    string? Version,
    string? AssetName);

/// <summary>
/// 版本对话框要显示的内容：一个已验证版本 + 一个最新版本。任一项不可用时用
/// <see cref="DependencyVersionOption.UnavailableReason"/> 说明原因，而不是从列表里消失。
/// </summary>
public sealed record DependencyVersionOption(
    string Choice,
    bool Available,
    string? Version,
    string? AssetName,
    string? UnavailableReason);

public sealed record DependencyVersionOptions(
    string Id,
    string SourceKind,
    IReadOnlyList<DependencyVersionOption> Options);

public sealed record OptionalDependencyStatus(
    OptionalDependencyDefinition Definition,
    string State,
    string DependencyRoot,
    string InstallerDirectory,
    string InstallDirectory,
    string? InstallerPath,
    bool InstallerAvailable,
    bool Installed,
    bool CanDownload,
    bool CanLaunchInstaller,
    string EffectiveInstallDirectory,
    string? DetectedInstallDirectory,
    string DetectionSource,
    string Message,
    /// <summary>安装器来源形态，界面据此如实标注按钮。</summary>
    string InstallerSourceKind = DependencyInstallerSourceKinds.Manual);

public sealed record OptionalDependencyDownloadRequest(
    bool AcknowledgeExternalTerms,
    string? VersionChoice = null);

public sealed record OptionalDependencyInstallRequest(
    bool AcknowledgeExternalTerms,
    string? VersionChoice = null);

public sealed record OptionalDependencyDownloadResult(
    string Id,
    string State,
    string FilePath,
    long BytesWritten,
    string Message,
    FileChangeReport? FileChanges = null);

public sealed record DependencyDownloadProgress(
    string Id,
    long BytesWritten,
    long? TotalBytes,
    double? Percent,
    double? SpeedBytesPerSecond);

public sealed record OptionalDependencyLaunchResult(
    string Id,
    string State,
    string InstallerPath,
    string Message);
