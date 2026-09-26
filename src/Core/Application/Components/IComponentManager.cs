using ResourceManager.App.Domain.Components;

namespace ResourceManager.App.Application.Components;

public interface IComponentManager
{
    Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(CancellationToken cancellationToken);

    Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken cancellationToken);

    Task<ComponentActionResult> DownloadAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken);

    Task<ComponentActionResult> InstallAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken);

    Task<ComponentActionResult> VerifyAsync(
        string id,
        CancellationToken cancellationToken);
}
