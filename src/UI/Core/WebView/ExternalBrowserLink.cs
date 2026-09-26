using System.Diagnostics;

namespace ResourceManager.NativeUi.WebView;

internal static class ExternalBrowserLink
{
    internal static bool TryOpen(string address, bool trustedDocument, bool userInitiated,
        Action<ProcessStartInfo> start)
    {
        if (!trustedDocument || !userInitiated
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return false;

        start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }
}
