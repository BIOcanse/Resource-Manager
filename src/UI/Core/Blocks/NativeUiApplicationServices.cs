using ResourceManager.NativeUi.WebView;

namespace ResourceManager.NativeUi;

// External resources of the window process. The control tree and all command
// handlers remain the same when tests supply isolated resources.
internal sealed class NativeUiApplicationServices
{
    internal string? ApiBaseAddress { get; init; }
    internal string? WebViewUserDataFolder { get; init; }
    internal string? FrontendPreferencesPath { get; init; }
    internal string? WebViewStartupFailurePath { get; init; }
    internal Func<BackendServiceSession>? CreateSession { get; init; }
    internal Func<WebView2HostConfiguration, IFrontendHost>? CreateFrontendHost { get; init; }
    internal Func<CancellationToken, Task>? BeforeProxyStart { get; init; }
    internal Action<MainForm, bool, bool>? PresentWindow { get; init; }
    /// <summary>The page's Task Manager replacement switch; omitted, the machine's IFEO value is written.</summary>
    internal Action<bool>? ApplyTaskManagerReplacement { get; init; }
}
