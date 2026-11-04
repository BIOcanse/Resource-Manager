namespace ResourceManager.App.Infrastructure.Updates;

/// <summary>The product's official release source and its permitted asset delivery hosts.</summary>
public static class ProductUpdateSource
{
    public const string Owner = "BIOcanse";
    public const string Repository = "Resource-Manager";
    public const string HttpClientName = "ResourceManager.TrustedUpdates";

    public static bool IsReleaseAssetUrl(string url, string owner, string repository,
        string? tag = null, string? assetName = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSecureUrl(uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var prefix = $"/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/download/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath[prefix.Length..].Split('/');
        return segments.Length == 2 && segments.All(segment => segment.Length > 0)
            && (tag is null || Uri.UnescapeDataString(segments[0]) == tag)
            && (assetName is null || Uri.UnescapeDataString(segments[1]) == assetName);
    }

    public static void EnsureUnredirectedResponse(HttpResponseMessage response, Uri requestedUri)
    {
        if ((int)response.StatusCode is >= 300 and < 400
            || response.RequestMessage?.RequestUri is { } actual && actual != requestedUri)
            throw new InvalidDataException("The release source redirected the request.");
    }

    /// <summary>Follows only the official repository's download redirects to GitHub's asset hosts.
    /// The supplied client must have automatic redirects disabled.</summary>
    public static async Task<HttpResponseMessage> SendAssetAsync(
        HttpClient client, string url, CancellationToken cancellationToken)
    {
        if (!IsReleaseAssetUrl(url, Owner, Repository))
            throw new InvalidDataException("The update asset is outside the official release source.");
        var uri = new Uri(url);
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            try
            {
                // Reject automatic redirect following even if a misconfigured client is supplied.
                if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
                    throw new InvalidDataException("The update client followed an unvalidated redirect.");
                if ((int)response.StatusCode is not (>= 300 and < 400))
                {
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                var location = response.Headers.Location;
                if (redirects == 5 || location is null)
                    throw new InvalidDataException("The update asset redirect limit was reached or its target is missing.");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsSecureUrl(next) || next.Fragment.Length != 0
                    || !(IsReleaseAssetUrl(next.AbsoluteUri, Owner, Repository)
                        || next.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                        || next.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                        || next.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The update asset redirected outside GitHub's trusted delivery hosts.");
                uri = next;
            }
            catch
            {
                response.Dispose();
                throw;
            }
            response.Dispose();
        }
        throw new InvalidDataException("The update asset redirect limit was reached.");
    }

    private static bool IsSecureUrl(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;
}
