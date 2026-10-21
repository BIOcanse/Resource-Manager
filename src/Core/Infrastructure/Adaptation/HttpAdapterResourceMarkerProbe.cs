using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed class HttpAdapterResourceMarkerProbe : IAdapterResourceMarkerProbe
{
    private readonly HttpClient httpClient;

    public HttpAdapterResourceMarkerProbe(HttpClient httpClient)
    {
        this.httpClient = httpClient;
        this.httpClient.Timeout = TimeSpan.FromSeconds(1.5);
    }

    public async Task<AdapterResourceMarkerProbeResult> ProbeAsync(
        AdapterSoftwareRegistrationRequest registration,
        AdapterRegistrationCaller caller,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.Now;
        var validation = AdapterRegistrationValidator.Validate(registration);
        if (validation is not null)
        {
            return new AdapterResourceMarkerProbeResult(
                AdapterResourceMarkerStates.Unsupported,
                checkedAt,
                null,
                validation);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, registration.ResourceMarkerEndpoint!.Address);
            request.Options.Set(HttpAdapterRegistrationConnection.RegistrationKey, registration);
            request.Options.Set(HttpAdapterRegistrationConnection.CallerKey, caller);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (!request.Options.TryGetValue(HttpAdapterRegistrationConnection.OwnershipKey, out var ownership)
                || !ownership.StillOwned(HttpAdapterRegistrationConnection.ReadProcess))
                return new AdapterResourceMarkerProbeResult(AdapterResourceMarkerStates.Unsupported, checkedAt,
                    statusCode, "Resource endpoint or declared process ownership could not be verified.");
            return response.IsSuccessStatusCode
                ? new AdapterResourceMarkerProbeResult(
                    AdapterResourceMarkerStates.Online,
                    checkedAt,
                    statusCode,
                    "Resource marker endpoint responded.") { OwnershipVerified = true }
                : new AdapterResourceMarkerProbeResult(
                    AdapterResourceMarkerStates.Unreachable,
                    checkedAt,
                    statusCode,
                    $"Resource marker endpoint returned HTTP {statusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
            or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return new AdapterResourceMarkerProbeResult(
                AdapterResourceMarkerStates.Unreachable,
                checkedAt,
                null,
                ex.Message);
        }
    }
}
