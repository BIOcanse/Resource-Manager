namespace ResourceManager.NativeUi.Localization;

/// <summary>
/// NativeUi 全部用户可见文案。每种语言提供一份完整实例，所有字段都是 required，
/// 缺翻译会在编译期暴露，消费端读到的永远是当前语言的确定字符串，不需要判空或回退。
/// 名字里带 Format 的字段是 <see cref="string.Format(string, object?[])"/> 模板。
/// </summary>
internal sealed record NativeText
{
    // 应用与窗口
    public required string AppName { get; init; }
    public required string WindowTitleFormat { get; init; }
    public required string TrayTooltipFormat { get; init; }

    // 可用性面板
    public required string AvailabilityPanelName { get; init; }
    public required string ConnectingTitle { get; init; }
    public required string ReconnectingTitle { get; init; }
    public required string ConnectingDetail { get; init; }
    public required string RetryButton { get; init; }
    public required string RetryButtonDescription { get; init; }
    public required string DiagnosticsButton { get; init; }
    public required string DiagnosticsButtonDescription { get; init; }
    public required string ExitButton { get; init; }
    public required string ExitButtonDescription { get; init; }
    public required string BackendUnavailableTitle { get; init; }
    public required string BackendUnavailableDetail { get; init; }
    public required string FrontendLoadingTitle { get; init; }
    public required string FrontendLoadingDetail { get; init; }
    public required string FrontendUnavailableTitle { get; init; }
    public required string FrontendUnavailableDetail { get; init; }

    // 外壳状态与动作
    public required string StatusConnecting { get; init; }
    public required string StatusReconnecting { get; init; }
    public required string StatusBackendUnavailable { get; init; }
    public required string StatusShuttingDown { get; init; }
    public required string StatusBackendReady { get; init; }
    public required string StatusFrontendLoading { get; init; }
    public required string StatusReady { get; init; }
    public required string StatusFrontendUnavailable { get; init; }
    public required string ActionReconnectBackend { get; init; }
    public required string ActionReloadFrontend { get; init; }

    // 本地服务会话与监视
    public required string BackendProcessExitedFormat { get; init; }
    public required string BackendHealthProbeFailedFormat { get; init; }
    public required string BackendStartFailedFormat { get; init; }
    public required string BackendLifecycleMonitorFailedFormat { get; init; }
    public required string BackendHttpErrorFormat { get; init; }
    public required string BackendEntryMissing { get; init; }
    public required string BackendNotOwned { get; init; }
    public required string BackendPipelineNotReady { get; init; }
    public required string NoProcessToTerminate { get; init; }
    public required string TerminateRequestCompleted { get; init; }

    // WebView 与前端资源
    public required string WebViewInitializationFailed { get; init; }
    public required string FrontendIdentityVerificationFailed { get; init; }
    public required string FrontendLoadFailed { get; init; }
    public required string WebViewRuntimeMissing { get; init; }

    // 托盘
    public required string TrayBackendStatusConnecting { get; init; }
    public required string TrayStatusFormat { get; init; }
    public required string TrayOpen { get; init; }
    public required string TrayReconnectBackend { get; init; }
    public required string TrayExit { get; init; }
    public required string BackendUnavailableBalloonTitle { get; init; }
    public required string NoVerifiedSession { get; init; }
    public required string DiagnosticsOpenFailed { get; init; }

    // 目录选择
    public required string SelectSoftwareRootFolder { get; init; }

    // 强制结束快捷键
    public required string ForceTerminateHotkeyBalloonTitle { get; init; }
    public required string NoForegroundProcessToTerminate { get; init; }
    public required string BackendUnavailableNoTerminate { get; init; }
    public required string ForceTerminateFailed { get; init; }
    public required string ForceTerminateHotkeyDisabled { get; init; }
    public required string ForceTerminateHotkeyEnableFailed { get; init; }
    public required string HotkeyNeedsAtLeastOneKey { get; init; }
    public required string ForceTerminateHotkeyNeedsModifier { get; init; }
    public required string KeyboardHookRegisterFailed { get; init; }
    public required string MouseHookRegisterFailed { get; init; }

    // 任务管理器快捷键替换
    public required string TaskManagerHotkeyReplacement { get; init; }
    public required string TaskManagerHotkeyEnableFailed { get; init; }
    public required string TaskManagerHotkeyDisableFailed { get; init; }
    public required string TaskManagerHotkeyForeignSetting { get; init; }
    public required string TaskManagerHotkeyNeedsAdminToEnable { get; init; }
    public required string TaskManagerHotkeyNeedsAdminToDisable { get; init; }
    public required string TaskManagerHotkeyEnabled { get; init; }
    public required string TaskManagerHotkeyDisabled { get; init; }
    public required string TaskManagerShortcutHookRegisterFailed { get; init; }
    public required string TaskManagerShortcutHookRemoveFailed { get; init; }
}
