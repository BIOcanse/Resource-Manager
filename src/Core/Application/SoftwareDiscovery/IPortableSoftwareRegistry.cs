using ResourceManager.App.Domain.SoftwareDiscovery;

namespace ResourceManager.App.Application.SoftwareDiscovery;

public interface IPortableSoftwareRegistry
{
    void Observe(PortableSoftwareObservation observation);

    IReadOnlyList<PortableSoftwareRegistration> GetSnapshot();

    Task<IReadOnlyList<PortableSoftwareRegistration>> RefreshAsync(CancellationToken cancellationToken);

    Task<PortableSoftwareRootConfirmationResult> ConfirmRootPathAsync(
        PortableSoftwareRootConfirmationRequest request,
        CancellationToken cancellationToken);
}
