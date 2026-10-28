using ResourceManager.App.Domain.SoftwareDiscovery;

namespace ResourceManager.App.Application.SoftwareDiscovery;

public interface IPortableSoftwareRegistry
{
    /// <summary>Waits cancellably for initialization, then admits the observation through the native registry.</summary>
    Task ObserveAsync(PortableSoftwareObservation observation, CancellationToken cancellationToken);

    /// <summary>Returns current registrations, or an empty no-value result before the first plan initializes the registry.</summary>
    IReadOnlyList<PortableSoftwareRegistration> GetSnapshot();

    Task<IReadOnlyList<PortableSoftwareRegistration>> RefreshAsync(CancellationToken cancellationToken);

    Task<PortableSoftwareRootConfirmationResult> ConfirmRootPathAsync(
        PortableSoftwareRootConfirmationRequest request,
        CancellationToken cancellationToken);
}
