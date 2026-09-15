namespace ResourceManager.NativeUi.Localization.Texts;

internal static class EnUs
{
    public static readonly NativeText Text = new()
    {
        AppName = "Resource Manager",
        WindowTitleFormat = "Resource Manager - {0}",
        TrayTooltipFormat = "Resource Manager - {0}",

        AvailabilityPanelName = "Local service status",
        ConnectingTitle = "Connecting to the local service",
        ReconnectingTitle = "Reconnecting to the local service",
        ConnectingDetail = "Verifying the service identity and establishing a session.",
        RetryButton = "Retry",
        RetryButtonDescription = "Reconnect to the local service now",
        DiagnosticsButton = "Open diagnostics folder",
        DiagnosticsButtonDescription = "Open the local diagnostics file folder",
        ExitButton = "Exit",
        ExitButtonDescription = "Exit Resource Manager",
        BackendUnavailableTitle = "The local service is temporarily unavailable",
        BackendUnavailableDetail = "Resource Manager keeps trying to connect in the background.",
        FrontendLoadingTitle = "Loading the interface",
        FrontendLoadingDetail = "The local service is verified; loading the application interface.",
        FrontendUnavailableTitle = "The application interface is temporarily unavailable",
        FrontendUnavailableDetail = "The local service is still running, so the interface can be reloaded.",

        StatusConnecting = "Connecting to the local service",
        StatusReconnecting = "Reconnecting to the local service",
        StatusBackendUnavailable = "Local service unavailable",
        StatusShuttingDown = "Exiting",
        StatusBackendReady = "Local service ready",
        StatusFrontendLoading = "Loading the interface",
        StatusReady = "Ready",
        StatusFrontendUnavailable = "Interface unavailable",
        ActionReconnectBackend = "Reconnect to the local service",
        ActionReloadFrontend = "Reload the interface",

        BackendProcessExitedFormat = "The local service process exited with code {0}.",
        BackendHealthProbeFailedFormat = "The local service failed {0} consecutive health probes.",
        BackendStartFailedFormat = "The local service failed to start: {0}",
        BackendLifecycleMonitorFailedFormat = "Local service lifecycle monitoring failed: {0}",
        BackendHttpErrorFormat = "The local service returned HTTP {0}.",
        BackendEntryMissing = "The local backend service entry point is missing from the final image.",
        BackendNotOwned = "The local backend service is not ready; the native UI does not own service startup. Start or repair ResourceManager.Service.",
        BackendPipelineNotReady = "The local service request pipeline is not ready.",
        NoProcessToTerminate = "There is no process to terminate.",
        TerminateRequestCompleted = "The process termination request completed.",

        WebViewInitializationFailed = "The embedded interface failed to initialize. Open the diagnostics folder for details.",
        FrontendIdentityVerificationFailed = "Frontend asset identity verification failed. Open the diagnostics folder for details.",
        FrontendLoadFailed = "The local interface could not be loaded from the verified service.",
        WebViewRuntimeMissing = "No compatible WebView2 runtime was found. Install the shared runtime and try again.",

        TrayBackendStatusConnecting = "Local service: connecting",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Open",
        TrayReconnectBackend = "Reconnect to the local service",
        TrayExit = "Exit",
        BackendUnavailableBalloonTitle = "Local service unavailable",
        NoVerifiedSession = "No verifiable local service session has been established yet.",
        DiagnosticsOpenFailed = "The diagnostics folder could not be opened.",

        SelectSoftwareRootFolder = "Select the application root folder",

        ForceTerminateHotkeyBalloonTitle = "Force-terminate hotkey",
        NoForegroundProcessToTerminate = "There is no foreground or unresponsive application to terminate.",
        BackendUnavailableNoTerminate = "The local service is unavailable, so no process was terminated.",
        ForceTerminateFailed = "Force termination failed. Try again later.",
        ForceTerminateHotkeyDisabled = "The force-terminate hotkey is disabled: it must include Ctrl, Alt or Win together with a non-modifier key.",
        ForceTerminateHotkeyEnableFailed = "The force-terminate hotkey could not be enabled. Check the key combination and try again.",
        HotkeyNeedsAtLeastOneKey = "A hotkey needs at least one key.",
        ForceTerminateHotkeyNeedsModifier = "The force-terminate hotkey must include Ctrl, Alt or Win together with a non-modifier key.",
        KeyboardHookRegisterFailed = "The custom global keyboard hotkey hook could not be registered.",
        MouseHookRegisterFailed = "The custom global mouse hotkey hook could not be registered.",

        TaskManagerHotkeyReplacement = "Task Manager hotkey replacement",
        TaskManagerHotkeyEnableFailed = "The Task Manager hotkey could not be enabled. Try again later.",
        TaskManagerHotkeyDisableFailed = "The Task Manager hotkey could not be disabled. Try again later.",
        TaskManagerHotkeyForeignSetting = "Another Task Manager hotkey setting was detected and has been left unchanged.",
        TaskManagerHotkeyNeedsAdminToEnable = "Administrator permission is required to take over the Task Manager hotkey while Resource Manager is not running.",
        TaskManagerHotkeyNeedsAdminToDisable = "Administrator permission is required to fully turn off Task Manager hotkey replacement.",
        TaskManagerHotkeyEnabled = "The Task Manager hotkey is enabled.",
        TaskManagerHotkeyDisabled = "The Task Manager hotkey is disabled.",
        TaskManagerShortcutHookRegisterFailed = "The Ctrl+Shift+Esc hotkey hook could not be registered.",
        TaskManagerShortcutHookRemoveFailed = "The Ctrl+Shift+Esc hotkey hook could not be removed."
    };
}
