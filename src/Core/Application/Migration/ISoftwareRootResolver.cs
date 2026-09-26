using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Application.Migration;

public interface ISoftwareRootResolver
{
    Task<SoftwareRootResolution> ResolveAsync(
        SoftwareRootResolutionRequest request,
        CancellationToken cancellationToken);
}
