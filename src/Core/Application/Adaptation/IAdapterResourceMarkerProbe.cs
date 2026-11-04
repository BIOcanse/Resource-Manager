using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Adaptation;

public interface IAdapterResourceMarkerProbe
{
    Task<AdapterResourceMarkerProbeResult> ProbeAsync(
        AdapterSoftwareRegistrationRequest registration,
        AdapterRegistrationCaller caller,
        CancellationToken cancellationToken);
}

// Created by the authenticated endpoint from Windows, never bound from a request body.
public sealed record AdapterRegistrationCaller(string WindowsSid, bool IsAdministrator);
