using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public interface ISoftwareRegistryView
{
    Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken);
}
