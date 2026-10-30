using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Dependencies;

public interface IOptionalDependencyManager
{
    Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(
        string? windowsSid,
        CancellationToken cancellationToken)
        => GetStatusesAsync(cancellationToken);

    Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken);

    Task<OptionalDependencyStatus?> GetStatusAsync(
        string id,
        string? windowsSid,
        CancellationToken cancellationToken)
        => GetStatusAsync(id, cancellationToken);

    /// <summary>
    /// Drops the held probe result so the next status read probes the files again. Called when an
    /// install, download, removal, repair or verify completes, or a caller asks for a refresh.
    /// </summary>
    void InvalidateStatuses() { }

    Task<DependencyVersionOptions> GetVersionOptionsAsync(string id, CancellationToken cancellationToken);

    Task<OptionalDependencyDownloadResult> DownloadAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        IProgress<DependencyDownloadProgress>? progress = null);

    Task<OptionalDependencyLaunchResult> LaunchInstallerAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        SoftwareOperationCaller? caller = null);
}
