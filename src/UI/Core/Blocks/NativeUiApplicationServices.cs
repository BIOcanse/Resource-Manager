using ResourceManager.NativeUi.Configuration;
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
    internal string? WebViewStartupFailurePath { get; init; }
    internal FrontendPreferencesOwner? Preferences { get; init; }
    internal INativeUiHotkeys? Hotkeys { get; init; }
    internal Func<BackendServiceSession>? CreateSession { get; init; }
    internal Func<BackendServiceSession, CancellationToken, Task>? ConnectSession { get; init; }
    internal Func<TimeSpan, CancellationToken, Task>? ReconnectDelay { get; init; }
    internal Func<WebView2HostConfiguration, IFrontendHost>? CreateFrontendHost { get; init; }
    internal Func<CancellationToken, Task>? BeforeProxyStart { get; init; }
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

// Both hotkey owners consume the same app-settings.json read: one watcher, one read per change.
internal sealed class NativeUiHotkeys(
    AppSettingsFileObserver settings,
    TaskManagerShortcutReplacementController taskManager,
    ForceTerminateHotkeyController forceTerminate) : INativeUiHotkeys
{
    public void Start()
    {
        settings.Subscribe(taskManager.Apply);
        settings.Subscribe(forceTerminate.Apply);
        settings.Start();
    }
    public void SetTaskManagerEnabled(bool enabled) => taskManager.SetEnabledFromUi(enabled);
    // The frontend asks after saving a hotkey; read even if the watcher already saw the save.
    public void ReloadForceTerminate() => settings.RequestRead(force: true);
    public void Dispose()
    {
        settings.Dispose();
        taskManager.Dispose();
        forceTerminate.Dispose();
    }
}
