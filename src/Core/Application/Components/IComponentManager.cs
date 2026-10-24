using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Components;

public interface IComponentManager
{
    Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(
        string? windowsSid,
        CancellationToken cancellationToken)
        => GetStatusesAsync(cancellationToken);

    Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken cancellationToken);

    Task<ComponentStatus?> GetStatusAsync(
        string id,
        string? windowsSid,
        CancellationToken cancellationToken)
        => GetStatusAsync(id, cancellationToken);

    Task<ComponentActionResult> DownloadAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        IProgress<DependencyDownloadProgress>? progress = null);

    Task<ComponentActionResult> InstallAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        SoftwareOperationCaller? caller = null,
        IProgress<DependencyDownloadProgress>? progress = null);

    Task<ComponentActionResult> VerifyAsync(
        string id,
        CancellationToken cancellationToken);
}
