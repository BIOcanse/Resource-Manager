using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

/// <summary>Endpoint fixtures that only need the metric catalog: no component is installed.</summary>
internal sealed class NoInstalledComponentManager : IComponentManager
{
    public Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ComponentStatus>>([]);

    public Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
        => Task.FromResult<ComponentStatus?>(null);

    public Task<ComponentActionResult> DownloadAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
        CancellationToken cancellationToken, IProgress<DependencyDownloadProgress>? progress = null) => throw new NotSupportedException();

    public Task<ComponentActionResult> InstallAsync(string id, bool acknowledgeExternalTerms, string? versionChoice,
        CancellationToken cancellationToken, SoftwareOperationCaller? caller = null, IProgress<DependencyDownloadProgress>? progress = null) => throw new NotSupportedException();

    public Task<ComponentActionResult> VerifyAsync(string id, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
