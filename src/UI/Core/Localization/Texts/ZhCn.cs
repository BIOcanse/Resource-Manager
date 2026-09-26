namespace ResourceManager.NativeUi.Localization.Texts;

internal static class ZhCn
{
    public static readonly NativeText Text = new()
    {
        AppName = "资源管理器",
        WindowTitleFormat = "资源管理器 - {0}",
        TrayTooltipFormat = "资源管理器 - {0}",

        AvailabilityPanelName = "本地服务状态",
        ConnectingTitle = "正在连接本地服务",
        ReconnectingTitle = "正在重新连接本地服务",
        ConnectingDetail = "正在验证服务身份并建立会话。",
        RetryButton = "重试",
        RetryButtonDescription = "立即重新连接本地服务",
        DiagnosticsButton = "打开诊断目录",
        DiagnosticsButtonDescription = "打开本地诊断文件目录",
        ExitButton = "退出",
        ExitButtonDescription = "退出资源管理器",
        BackendUnavailableTitle = "本地服务暂时不可用",
        BackendUnavailableDetail = "资源管理器会在后台继续尝试连接。",
        FrontendLoadingTitle = "正在加载界面",
        FrontendLoadingDetail = "本地服务已通过身份验证，正在加载应用界面。",
        FrontendUnavailableTitle = "应用界面暂时不可用",
        FrontendUnavailableDetail = "本地服务仍在运行，可以重新加载界面。",

        StatusConnecting = "正在连接本地服务",
        StatusReconnecting = "正在重新连接本地服务",
        StatusBackendUnavailable = "本地服务不可用",
        StatusShuttingDown = "正在退出",
        StatusBackendReady = "本地服务已就绪",
        StatusFrontendLoading = "正在加载界面",
        StatusReady = "已就绪",
        StatusFrontendUnavailable = "界面不可用",
        ActionReconnectBackend = "重新连接本地服务",
        ActionReloadFrontend = "重新加载界面",

        BackendProcessExitedFormat = "本地服务进程已退出，退出码 {0}。",
        BackendHealthProbeFailedFormat = "本地服务连续 {0} 次健康探测失败。",
        BackendStartFailedFormat = "本地服务启动失败：{0}",
        BackendLifecycleMonitorFailedFormat = "本地服务生命周期监视失败：{0}",
        BackendHttpErrorFormat = "本地服务返回 HTTP {0}。",
        BackendEntryMissing = "最终映像中缺少本地后端服务入口。",
        BackendNotOwned = "本地后端服务尚未就绪；Native UI 不拥有服务启动权限。请启动或修复 ResourceManager.Service。",
        BackendPipelineNotReady = "本地服务请求管线尚未就绪。",
        NoProcessToTerminate = "没有可结束的进程。",
        TerminateRequestCompleted = "进程终止请求已完成。",

        WebViewInitializationFailed = "内嵌界面初始化失败。可以打开诊断目录查看详细信息。",
        FrontendIdentityVerificationFailed = "前端资源身份验证失败。可以打开诊断目录查看详细信息。",
        FrontendLoadFailed = "本地界面未能从已验证的服务加载。",
        WebViewRuntimeMissing = "没有找到兼容的 WebView2 Runtime。请安装共享运行时后重试。",

        TrayBackendStatusConnecting = "本地服务：正在连接",
        TrayStatusFormat = "状态：{0}",
        TrayOpen = "打开",
        TrayReconnectBackend = "重新连接本地服务",
        TrayExit = "退出",
        BackendUnavailableBalloonTitle = "本地服务不可用",
        NoVerifiedSession = "尚未建立可验证的本地服务会话。",
        DiagnosticsOpenFailed = "无法打开诊断目录。",

        SelectSoftwareRootFolder = "选择软件根目录",

        ForceTerminateHotkeyBalloonTitle = "强制结束快捷键",
        NoForegroundProcessToTerminate = "没有可结束的前台或无响应程序。",
        BackendUnavailableNoTerminate = "本地服务不可用，未执行进程终止。",
        ForceTerminateFailed = "强制结束失败，请稍后重试。",
        ForceTerminateHotkeyDisabled = "强制结束快捷键已停用：必须同时包含 Ctrl、Alt 或 Win 修饰键以及一个非修饰键。",
        ForceTerminateHotkeyEnableFailed = "强制结束快捷键启用失败，请检查按键设置后重试。",
        HotkeyNeedsAtLeastOneKey = "快捷键至少需要一个按键。",
        ForceTerminateHotkeyNeedsModifier = "强制结束快捷键必须同时包含 Ctrl、Alt 或 Win 修饰键以及一个非修饰键。",
        KeyboardHookRegisterFailed = "无法注册自定义全局键盘快捷键钩子。",
        MouseHookRegisterFailed = "无法注册自定义全局鼠标快捷键钩子。",

        TaskManagerHotkeyReplacement = "任务管理器快捷键替换",
        TaskManagerHotkeyEnableFailed = "任务管理器快捷键启用失败，请稍后重试。",
        TaskManagerHotkeyDisableFailed = "任务管理器快捷键关闭失败，请稍后重试。",
        TaskManagerHotkeyForeignSetting = "检测到其他任务管理器快捷键设置，已保留原设置。",
        TaskManagerHotkeyNeedsAdminToEnable = "需要管理员权限才能在资源管理器未运行时接管任务管理器快捷键。",
        TaskManagerHotkeyNeedsAdminToDisable = "需要管理员权限才能完全关闭任务管理器快捷键替换。",
        TaskManagerHotkeyEnabled = "任务管理器快捷键已启用。",
        TaskManagerHotkeyDisabled = "任务管理器快捷键已关闭。",
        TaskManagerShortcutHookRegisterFailed = "无法注册 Ctrl+Shift+Esc 快捷键钩子。",
        TaskManagerShortcutHookRemoveFailed = "无法移除 Ctrl+Shift+Esc 快捷键钩子。"
    };
}
