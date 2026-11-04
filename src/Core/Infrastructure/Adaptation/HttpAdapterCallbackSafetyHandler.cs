using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed class HttpAdapterCallbackSafetyHandler(IServer server) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = request.RequestUri;
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (target is null || !target.IsAbsoluteUri || !target.IsLoopback
            || target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 || target.Fragment.Length != 0
            || addresses is null || addresses.Count == 0
            || addresses.Any(address => Uri.TryCreate(address, UriKind.Absolute, out var own) && own.Port == target.Port))
            throw new HttpRequestException("Adapter callbacks cannot target the service or leave loopback.");
        return base.SendAsync(request, cancellationToken);
    }
}
