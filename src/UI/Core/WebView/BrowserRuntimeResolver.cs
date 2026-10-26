using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using ResourceManager.NativeUi.Localization;
using ResourceManager.Shared.Localization;

namespace ResourceManager.NativeUi.WebView;

internal sealed record BrowserRuntimeResolution(
    string? BrowserExecutableFolder,
    string Version,
    string Source);

internal static class BrowserRuntimeResolver
{
    public static Task<BrowserRuntimeResolution> ResolveAsync(CancellationToken cancellationToken = default)
        => ResolveAsync(ProbeSystem, () => ConfirmInstallation(cancellationToken), InstallSystemRuntimeAsync,
            cancellationToken);

    internal static Task<BrowserRuntimeResolution> ResolveAsync(
        Func<string?> probeSystem, Func<bool> confirmInstallation, Func<Task> installSystemRuntime,
        CancellationToken cancellationToken = default)
        => ResolveAsync(probeSystem, confirmInstallation,
            token => installSystemRuntime().WaitAsync(token), cancellationToken);

    internal static async Task<BrowserRuntimeResolution> ResolveAsync(
        Func<string?> probeSystem, Func<bool> confirmInstallation,
        Func<CancellationToken, Task> installSystemRuntime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = probeSystem();
        if (string.IsNullOrWhiteSpace(version))
        {
            var consented = confirmInstallation();
            cancellationToken.ThrowIfCancellationRequested();
            if (!consented)
                throw new BrowserRuntimeInstallationDeclinedException(
                    ToolText.For(NativeUiText.Language).WebViewInstallCancelled);
            await installSystemRuntime(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            version = probeSystem();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException(
                    "WebView2 setup completed, but the system runtime is not available. Restart Resource Manager and try again.");
        }

        return new BrowserRuntimeResolution(null, version, "SystemShared");
    }

    internal static bool ConfirmInstallation(CancellationToken cancellationToken,
        Func<TaskDialogPage, TaskDialogButton>? showDialog = null,
        Action<TaskDialogPage>? closeDialog = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = CreateConsentPage(ToolText.For(NativeUiText.Language));
        closeDialog ??= static page => page.BoundDialog?.Close();
        // TaskDialog runs a nested message loop and must be closed on its STA.
        // Disposing this invisible dispatcher also releases any queued close.
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        using var registration = cancellationToken.Register(() =>
            dispatcher.BeginInvoke(new Action(() => closeDialog(page))));
        page.Created += (_, _) =>
        {
            // Cancellation can arrive before the native dialog has a handle.
            if (cancellationToken.IsCancellationRequested) closeDialog(page);
        };
        var result = (showDialog ?? (static page => TaskDialog.ShowDialog(page)))(page);
        cancellationToken.ThrowIfCancellationRequested();
        return result == page.Buttons[0];
    }

    internal static TaskDialogPage CreateConsentPage(ToolText text)
    {
        var install = new TaskDialogButton(text.WebViewInstall);
        var cancel = new TaskDialogButton(text.Cancel);
        return new TaskDialogPage
        {
            Caption = NativeUiText.Current.AppName,
            Heading = text.WebViewInstallTitle,
            Text = text.WebViewInstallConsent,
            Buttons = { install, cancel },
            DefaultButton = cancel,
            AllowCancel = true
        };
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

    private static async Task InstallSystemRuntimeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            Path.Combine(NativeUiPaths.WebViewUserRoot, "RuntimeInstallers") })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("WebView2 setup could not start.");
        // InstallWebView2Runtime.ps1 emits no success output and only its caught
        // error message. Read after exit so stop leaves no pipe-reader workers.
        // Disposing Process releases our handles; the consented installer lives on.
        await process.WaitForExitAsync(cancellationToken);
        Trace.WriteLine(await process.StandardOutput.ReadToEndAsync(cancellationToken));
        var detail = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Automatic WebView2 setup failed. Check your internet connection and try again. {detail.Trim()}");
    }
}
