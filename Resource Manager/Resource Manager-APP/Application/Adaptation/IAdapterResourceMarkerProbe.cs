using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Adaptation;

public interface IAdapterResourceMarkerProbe
{
    Task<AdapterResourceMarkerProbeResult> ProbeAsync(
        AdapterResourceMarkerEndpoint endpoint,
        CancellationToken cancellationToken);
}
