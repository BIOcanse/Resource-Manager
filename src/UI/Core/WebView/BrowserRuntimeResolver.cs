using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace ResourceManager.NativeUi.WebView;

internal sealed record BrowserRuntimeResolution(
    string? BrowserExecutableFolder,
    string Version,
    string Source);

internal static class BrowserRuntimeResolver
{
    public static Task<BrowserRuntimeResolution> ResolveAsync()
        => ResolveAsync(ProbeSystem, InstallSystemRuntimeAsync);

    internal static async Task<BrowserRuntimeResolution> ResolveAsync(
        Func<string?> probeSystem, Func<Task> installSystemRuntime)
    {
        var version = probeSystem();
        if (string.IsNullOrWhiteSpace(version))
        {
            await installSystemRuntime();
            version = probeSystem();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException(
                    "WebView2 setup completed, but the system runtime is not available. Restart Resource Manager and try again.");
        }

        return new BrowserRuntimeResolution(null, version, "SystemShared");
    }

    private static string? ProbeSystem()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception ex) when (
            ex is WebView2RuntimeNotFoundException
                or ArgumentException
                or InvalidOperationException
                or COMException)
        {
            return null;
        }
    }

    private static async Task InstallSystemRuntimeAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "InstallWebView2Runtime.ps1");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-DownloadDirectory",
            Path.Combine(NativeUiPaths.PackageRoot, "Dependencies", "shared-webview2-runtime", "installer") })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("WebView2 setup could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Trace.WriteLine(await output);
        var detail = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Automatic WebView2 setup failed. Check your internet connection and try again. {detail.Trim()}");
    }
}
