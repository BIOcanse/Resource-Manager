using ResourceManager.App.Domain.PublicServices;

namespace ResourceManager.App.Application.PublicServices;

public interface ILocalServiceCatalogQueries
{
    Task<LocalPublicServiceDescriptor> GetCatalogAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalPublicServiceCapability>> ListCapabilitiesAsync(
        CancellationToken cancellationToken);
}
