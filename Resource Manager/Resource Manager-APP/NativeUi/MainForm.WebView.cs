using ResourceManager.NativeUi.WebView;
using System.Text.Json;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private const string AppUrl = "http://127.0.0.1:9321/";
    private const string ApiRequestFilter = "http://127.0.0.1:9321/api/*";
    private const string AccessTokenHeaderName = "X-Resource-Manager-Token";

    private async Task<bool> EnsureFrontendHostAsync()
    {
        if (frontendHost.IsReady)
        {
            return true;
        }

        frontendHostInitializationTask ??= InitializeFrontendHostAsync();
        var initialized = await frontendHostInitializationTask;
        if (!initialized)
        {
            frontendHostInitializationTask = null;
        }

        return initialized;
    }

    private async Task<bool> InitializeFrontendHostAsync()
    {
        try
        {
            await frontendHost.InitializeAsync();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            WriteWebViewStartupFailure(ex);
            if (backendContentEnabled)
            {
                ShowFrontendUnavailable(
                    "内嵌界面初始化失败。可以打开诊断目录查看详细信息。");
            }
            return false;
        }
    }

    private static void WriteWebViewStartupFailure(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(NativeUiPaths.WebViewStartupFailurePath)
                ?? AppContext.BaseDirectory);
            File.WriteAllText(
                NativeUiPaths.WebViewStartupFailurePath,
                JsonSerializer.Serialize(
                    new
                    {
                        updatedAt = DateTimeOffset.Now,
                        exceptionType = exception.GetType().FullName,
                        exception.Message,
                        exception.HResult,
                        exception.StackTrace
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        catch (Exception diagnosticsException) when (
            diagnosticsException is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static void ClearWebViewStartupFailure()
    {
        try
        {
            File.Delete(NativeUiPaths.WebViewStartupFailurePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void HandleFrontendMessageReceived(
        object? sender,
        FrontendMessageReceivedEventArgs args)
    {
        HandleWebMessage(args.Json);
    }

    private async void HandleFrontendNavigationCompleted(
        object? sender,
        FrontendNavigationCompletedEventArgs args)
    {
        if (args.IsSuccess && args.IsTrustedDocument && backendContentEnabled)
        {
            ClearWebViewStartupFailure();
            PublishBackendSessionProjection();
            ShowFrontendReady();
            lastPublishedHostVisibility = null;
            PublishHostVisibility();
        }
        else if (backendContentEnabled && !args.WasConnectionAborted)
        {
            appNavigationStarted = false;
            ShowFrontendUnavailable("本地界面未能从已验证的服务加载。");
        }
    }

    private void PublishHostVisibility(bool forceHidden = false)
    {
        var visible = !forceHidden && Visible && WindowState != FormWindowState.Minimized;
        if (lastPublishedHostVisibility == visible || !frontendHost.IsReady)
        {
            return;
        }

        lastPublishedHostVisibility = visible;
        TryPostHostMessage(visible ? "host.visibility:visible" : "host.visibility:hidden");
    }

    private void PublishWindowResizeState(bool resizing)
    {
        TryPostHostMessage(resizing ? "host.window-resize:start" : "host.window-resize:end");
    }

    private void PublishBackendSessionProjection()
    {
        if (!frontendHost.IsReady || backendSessionProjection is null)
        {
            return;
        }

        try
        {
            frontendHost.PostJsonMessage(backendSessionProjection.ToJson());
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void TryPostHostMessage(string message)
    {
        try
        {
            frontendHost.PostStringMessage(message);
        }
        catch (InvalidOperationException)
        {
        }
    }

}
