using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using ResourceManager.Shared.BrowserRuntimes;

namespace ResourceManager.NativeUi.WebView;

internal sealed record BrowserRuntimeResolution(
    string? BrowserExecutableFolder,
    string Version,
    string Source);

internal static class BrowserRuntimeResolver
{
    public static BrowserRuntimeResolution Resolve()
    {
        if (TryProbe(browserExecutableFolder: null, out var systemVersion))
        {
            return new BrowserRuntimeResolution(null, systemVersion, "SystemShared");
        }

        var discovery = BrowserRuntimeDiscovery.Discover(NativeUiPaths.PackageRoot);
        var candidates = discovery.Candidates
            .Where(static candidate => candidate.Kind == BrowserRuntimeKinds.WebView2Runtime)
            .OrderBy(static candidate => candidate.Source == "Managed" ? 0 : 1)
            .ThenByDescending(static candidate => ParseVersion(candidate.Version))
            .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (TryProbe(candidate.RuntimeDirectory, out var version))
            {
                return new BrowserRuntimeResolution(
                    candidate.RuntimeDirectory,
                    version,
                    candidate.Source);
            }

            System.Diagnostics.Trace.WriteLine(
                $"WebView2 candidate rejected by compatibility probe: {candidate.RuntimeDirectory}");
        }

        throw new InvalidOperationException(
            "没有找到兼容的 WebView2 Runtime。请安装共享运行时后重试。");
    }

    private static bool TryProbe(string? browserExecutableFolder, out string version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString(browserExecutableFolder);
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (Exception ex) when (
            ex is WebView2RuntimeNotFoundException
                or ArgumentException
                or InvalidOperationException
                or COMException)
        {
            version = "";
            return false;
        }
    }

    private static Version ParseVersion(string value)
    {
        var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0.0";
        return Version.TryParse(token, out var parsed) ? parsed : new Version(0, 0);
    }
}
