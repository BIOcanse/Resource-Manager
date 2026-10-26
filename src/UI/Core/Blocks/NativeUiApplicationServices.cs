using ResourceManager.NativeUi.Preferences;
using ResourceManager.NativeUi.SystemIntegration;
using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;
using ResourceManager.NativeUi.WebView;

namespace ResourceManager.NativeUi;

// External resources of the native composition. The control tree and all command
// handlers remain the same when tests supply isolated resources.
internal sealed class NativeUiApplicationServices
{
    internal string? ApiBaseAddress { get; init; }
    internal string? WebViewUserDataFolder { get; init; }
    internal string? FrontendPreferencesPath { get; init; }
    internal FrontendPreferencesOwner? Preferences { get; init; }
    internal INativeUiHotkeys? Hotkeys { get; init; }
    internal Func<BackendServiceSession>? CreateSession { get; init; }
    internal Func<BackendServiceSession, CancellationToken, Task>? ConnectSession { get; init; }
    internal Func<TimeSpan, CancellationToken, Task>? ReconnectDelay { get; init; }
    internal Func<WebView2HostConfiguration, IFrontendHost>? CreateFrontendHost { get; init; }
    internal Func<ContextMenuStrip, NotifyIcon>? CreateTrayIcon { get; init; }
    internal Action<MainForm, bool, bool>? PresentWindow { get; init; }
    internal Func<BackendServiceSession, CancellationToken, Task>? ObserveDesktop { get; init; }
    internal Func<BackendServiceSession, CancellationToken, Task>? ObserveOverlay { get; init; }
    internal Func<BackendServiceSession, CancellationToken, Task>? ObserveTray { get; init; }
}

internal interface INativeUiHotkeys : IDisposable
{
    void Start();
    void SetTaskManagerEnabled(bool enabled);
    void ReloadForceTerminate();
}

internal sealed class NativeUiHotkeys(
    TaskManagerShortcutReplacementController taskManager,
    ForceTerminateHotkeyController forceTerminate) : INativeUiHotkeys
{
    public void Start()
    {
        taskManager.Start();
        forceTerminate.Start();
    }
    public void SetTaskManagerEnabled(bool enabled) => taskManager.SetEnabledFromUi(enabled);
    public void ReloadForceTerminate() => forceTerminate.Reload();
    public void Dispose()
    {
        taskManager.Dispose();
        forceTerminate.Dispose();
    }
}
