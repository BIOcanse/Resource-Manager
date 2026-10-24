using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.Software;

public interface ISoftwareRegistryView
{
    Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(
        string? windowsSid,
        CancellationToken cancellationToken,
        IReadOnlyList<RuntimeSoftwareAttribution>? runtimeSoftware = null)
        => GetSoftwareAsync(cancellationToken);

    Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(
        string? windowsSid,
        CancellationToken cancellationToken,
        IReadOnlyList<RuntimeSoftwareAttribution>? runtimeSoftware = null)
        => RefreshSoftwareAsync(cancellationToken);
}
