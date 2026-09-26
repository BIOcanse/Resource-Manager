using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Application.Dependencies;

public interface IOptionalDependencyManager
{
    Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken);

    Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken);

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
        CancellationToken cancellationToken);
}
