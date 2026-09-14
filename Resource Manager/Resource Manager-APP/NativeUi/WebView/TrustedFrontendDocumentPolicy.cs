namespace ResourceManager.NativeUi.WebView;

internal sealed class TrustedFrontendDocumentPolicy
{
    private readonly Uri applicationUri;

    public TrustedFrontendDocumentPolicy(string applicationAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationAddress);
        applicationUri = new Uri(applicationAddress, UriKind.Absolute);
        if (!IsHttpLoopback(applicationUri)
            || applicationUri.AbsolutePath != "/"
            || applicationUri.Query.Length != 0)
        {
            throw new ArgumentException(
                "The frontend application address must be an exact HTTP loopback root.",
                nameof(applicationAddress));
        }
    }

    public bool IsTrustedDocument(string? address)
    {
        return TryCreateAbsoluteUri(address, out var candidate)
            && HasApplicationOrigin(candidate)
            && candidate.AbsolutePath == "/"
            && candidate.Query.Length == 0;
    }

    public bool IsTrustedApiRequest(string? address)
    {
        if (!TryCreateAbsoluteUri(address, out var candidate)
            || !HasApplicationOrigin(candidate))
        {
            return false;
        }

        return candidate.AbsolutePath.Equals("/api", StringComparison.Ordinal)
            || candidate.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);
    }

    public static bool IsBlankDocument(string? address) =>
        string.Equals(address, "about:blank", StringComparison.OrdinalIgnoreCase);

    private bool HasApplicationOrigin(Uri candidate) =>
        candidate.Scheme.Equals(applicationUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && candidate.IdnHost.Equals(applicationUri.IdnHost, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == applicationUri.Port
        && candidate.UserInfo.Length == 0;

    private static bool TryCreateAbsoluteUri(string? address, out Uri uri)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var candidate)
            && IsHttpLoopback(candidate))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsHttpLoopback(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        && uri.IdnHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
