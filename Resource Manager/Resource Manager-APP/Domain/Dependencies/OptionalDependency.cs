using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Domain.Dependencies;

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
    string InstallNote);

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
    string Message);

public sealed record OptionalDependencyDownloadRequest(bool AcknowledgeExternalTerms);

public sealed record OptionalDependencyInstallRequest(bool AcknowledgeExternalTerms);

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
