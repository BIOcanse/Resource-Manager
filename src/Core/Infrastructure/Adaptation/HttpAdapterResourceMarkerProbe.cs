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
        AdapterResourceMarkerEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.Now;
        var validation = AdapterRegistrationValidator.ValidateResourceMarkerEndpoint(endpoint);
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
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Address);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var statusCode = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? new AdapterResourceMarkerProbeResult(
                    AdapterResourceMarkerStates.Online,
                    checkedAt,
                    statusCode,
                    "Resource marker endpoint responded.")
                : new AdapterResourceMarkerProbeResult(
                    AdapterResourceMarkerStates.Unreachable,
                    checkedAt,
                    statusCode,
                    $"Resource marker endpoint returned HTTP {statusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return new AdapterResourceMarkerProbeResult(
                AdapterResourceMarkerStates.Unreachable,
                checkedAt,
                null,
                ex.Message);
        }
    }
}
