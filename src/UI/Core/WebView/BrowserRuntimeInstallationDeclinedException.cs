namespace ResourceManager.NativeUi.WebView;

internal sealed class BrowserRuntimeInstallationDeclinedException(string reason)
    : OperationCanceledException(reason);
