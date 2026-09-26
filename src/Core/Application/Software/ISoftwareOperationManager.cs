using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public interface ISoftwareOperationManager
{
    Task<SoftwareOperationResult> UninstallAsync(
        SoftwareOperationRequest request,
        CancellationToken cancellationToken);
}
