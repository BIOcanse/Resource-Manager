namespace ResourceManager.NativeUi.Localization.Texts;

internal static class ZhTw
{
    public static readonly NativeText Text = new()
    {
        AppName = "資源管理器",
        WindowTitleFormat = "資源管理器 - {0}",
        TrayTooltipFormat = "資源管理器 - {0}",

        AvailabilityPanelName = "本機服務狀態",
        ConnectingTitle = "正在連線至本機服務",
        ReconnectingTitle = "正在重新連線至本機服務",
        ConnectingDetail = "正在驗證服務身分並建立工作階段。",
        RetryButton = "重試",
        RetryButtonDescription = "立即重新連線至本機服務",
        DiagnosticsButton = "開啟診斷資料夾",
        DiagnosticsButtonDescription = "開啟本機診斷檔案資料夾",
        ExitButton = "結束",
        ExitButtonDescription = "結束資源管理器",
        BackendUnavailableTitle = "本機服務暫時無法使用",
        BackendUnavailableDetail = "資源管理器會在背景繼續嘗試連線。",
        FrontendLoadingTitle = "正在載入介面",
        FrontendLoadingDetail = "本機服務已通過身分驗證，正在載入應用程式介面。",
        FrontendUnavailableTitle = "應用程式介面暫時無法使用",
        FrontendUnavailableDetail = "本機服務仍在執行，可以重新載入介面。",

        StatusConnecting = "正在連線至本機服務",
        StatusReconnecting = "正在重新連線至本機服務",
        StatusBackendUnavailable = "本機服務無法使用",
        StatusShuttingDown = "正在結束",
        StatusBackendReady = "本機服務已就緒",
        StatusFrontendLoading = "正在載入介面",
        StatusReady = "已就緒",
        StatusFrontendUnavailable = "介面無法使用",
        ActionReconnectBackend = "重新連線至本機服務",
        ActionReloadFrontend = "重新載入介面",

        BackendProcessExitedFormat = "本機服務處理程序已結束，結束代碼 {0}。",
        BackendHealthProbeFailedFormat = "本機服務連續 {0} 次健康檢查失敗。",
        BackendStartFailedFormat = "本機服務啟動失敗：{0}",
        BackendLifecycleMonitorFailedFormat = "本機服務生命週期監視失敗：{0}",
        BackendHttpErrorFormat = "本機服務傳回 HTTP {0}。",
        BackendEntryMissing = "最終映像中缺少本機後端服務進入點。",
        BackendNotOwned = "本機後端服務尚未就緒；Native UI 不擁有服務啟動權限。請啟動或修復 ResourceManager.Service。",
        BackendPipelineNotReady = "本機服務要求管線尚未就緒。",
        NoProcessToTerminate = "沒有可結束的處理程序。",
        TerminateRequestCompleted = "處理程序終止要求已完成。",

        WebViewInitializationFailed = "內嵌介面初始化失敗。可以開啟診斷資料夾查看詳細資訊。",
        FrontendIdentityVerificationFailed = "前端資源身分驗證失敗。可以開啟診斷資料夾查看詳細資訊。",
        FrontendLoadFailed = "本機介面未能從已驗證的服務載入。",
        WebViewRuntimeMissing = "找不到相容的 WebView2 Runtime。請安裝共用執行階段後再試一次。",

        TrayBackendStatusConnecting = "本機服務：正在連線",
        TrayStatusFormat = "狀態：{0}",
        TrayOpen = "開啟",
        TrayReconnectBackend = "重新連線至本機服務",
        TrayExit = "結束",
        BackendUnavailableBalloonTitle = "本機服務無法使用",
        NoVerifiedSession = "尚未建立可驗證的本機服務工作階段。",
        DiagnosticsOpenFailed = "無法開啟診斷資料夾。",

        SelectSoftwareRootFolder = "選擇軟體根目錄",

        ForceTerminateHotkeyBalloonTitle = "強制結束快速鍵",
        NoForegroundProcessToTerminate = "沒有可結束的前景或無回應程式。",
        BackendUnavailableNoTerminate = "本機服務無法使用，未執行處理程序終止。",
        ForceTerminateFailed = "強制結束失敗，請稍後再試。",
        ForceTerminateHotkeyDisabled = "強制結束快速鍵已停用：必須同時包含 Ctrl、Alt 或 Win 輔助鍵以及一個非輔助鍵。",
        ForceTerminateHotkeyEnableFailed = "強制結束快速鍵啟用失敗，請檢查按鍵設定後再試一次。",
        HotkeyNeedsAtLeastOneKey = "快速鍵至少需要一個按鍵。",
        ForceTerminateHotkeyNeedsModifier = "強制結束快速鍵必須同時包含 Ctrl、Alt 或 Win 輔助鍵以及一個非輔助鍵。",
        KeyboardHookRegisterFailed = "無法註冊自訂全域鍵盤快速鍵攔截。",
        MouseHookRegisterFailed = "無法註冊自訂全域滑鼠快速鍵攔截。",

        TaskManagerHotkeyReplacement = "工作管理員快速鍵取代",
        TaskManagerHotkeyEnableFailed = "工作管理員快速鍵啟用失敗，請稍後再試。",
        TaskManagerHotkeyDisableFailed = "工作管理員快速鍵關閉失敗，請稍後再試。",
        TaskManagerHotkeyForeignSetting = "偵測到其他工作管理員快速鍵設定，已保留原設定。",
        TaskManagerHotkeyNeedsAdminToEnable = "需要系統管理員權限才能在資源管理器未執行時接管工作管理員快速鍵。",
        TaskManagerHotkeyNeedsAdminToDisable = "需要系統管理員權限才能完全關閉工作管理員快速鍵取代。",
        TaskManagerHotkeyEnabled = "工作管理員快速鍵已啟用。",
        TaskManagerHotkeyDisabled = "工作管理員快速鍵已關閉。",
        TaskManagerShortcutHookRegisterFailed = "無法註冊 Ctrl+Shift+Esc 快速鍵攔截。",
        TaskManagerShortcutHookRemoveFailed = "無法移除 Ctrl+Shift+Esc 快速鍵攔截。"
    };
}
