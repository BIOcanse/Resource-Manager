using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Adaptation;

public interface IAdapterSoftwareRegistry
{
    Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(CancellationToken cancellationToken);

    Task<AdapterRegistrationResult> RegisterAsync(
        AdapterSoftwareRegistrationRequest request,
        AdapterResourceMarkerProbeResult markerProbe,
        CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string id, CancellationToken cancellationToken);
}

public sealed record AdapterRegistrationResult(
    AdapterSoftwareRegistration Registration,
    bool Created);
