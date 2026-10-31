using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ResourceManager.NativeUi;

/// <summary>
/// Opens a GitHub address in the browser or selects a local file in File Explorer when the user clicks in the web
/// frontend (docs/crash_dumps_2026-10-31.md: the backend never opens anything). Only https://github.com/ addresses and
/// existing local files given by an absolute path are accepted; every request gets one shell.open.result reply.
/// </summary>
internal static class ShellOpenRequests
{
    internal const string RejectedError = "rejected";
    internal const string NotFoundError = "not-found";
    internal const string FailedError = "failed";

    /// <summary>An absolute https address on github.com without user information or a non-default port.</summary>
    internal static bool TryGitHubAddress(string? address, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || parsed.UserInfo.Length != 0
            || !string.Equals(parsed.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// A fully qualified local drive path (no UNC or device path, no wildcard) in its full form. Whether the file
    /// exists is checked separately so the reply can say not-found.
    /// </summary>
    internal static bool TryLocalFilePath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0) return false;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return true;
    }

    /// <summary>explorer.exe from the Windows directory with /select, so the folder opens with the file selected.</summary>
    internal static ProcessStartInfo RevealStart(string fullPath) => new()
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
        Arguments = "/select,\"" + fullPath + "\"",
        UseShellExecute = false
    };

    internal static ProcessStartInfo BrowserStart(Uri uri) => new(uri.AbsoluteUri) { UseShellExecute = true };
}

public sealed partial class MainForm
{
    private void HandleShellOpenRequest(JsonElement message, bool url)
    {
        var requestId = message.TryGetProperty("requestId", out var requestIdProperty)
            && requestIdProperty.ValueKind == JsonValueKind.String ? requestIdProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId)) return;
        var value = message.TryGetProperty(url ? "url" : "path", out var valueProperty)
            && valueProperty.ValueKind == JsonValueKind.String ? valueProperty.GetString() : null;
        string? error = null;
        try
        {
            if (url)
            {
                if (!ShellOpenRequests.TryGitHubAddress(value, out var uri)) error = ShellOpenRequests.RejectedError;
                else Process.Start(ShellOpenRequests.BrowserStart(uri))?.Dispose();
            }
            else if (!ShellOpenRequests.TryLocalFilePath(value, out var fullPath)) error = ShellOpenRequests.RejectedError;
            else if (!File.Exists(fullPath)) error = ShellOpenRequests.NotFoundError;
            else Process.Start(ShellOpenRequests.RevealStart(fullPath))?.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.WriteLine(exception);
            error = ShellOpenRequests.FailedError;
        }
        var response = new Dictionary<string, object?>
        {
            ["type"] = "shell.open.result",
            ["requestId"] = requestId,
            ["opened"] = error is null,
            ["error"] = error
        };
        try
        {
            frontendHost.PostJsonMessage(JsonSerializer.Serialize(response));
        }
        catch (InvalidOperationException)
        {
        }
    }
}
