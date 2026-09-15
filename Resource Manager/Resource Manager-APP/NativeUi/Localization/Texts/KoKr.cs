namespace ResourceManager.NativeUi.Localization.Texts;

internal static class KoKr
{
    public static readonly NativeText Text = new()
    {
        AppName = "리소스 관리자",
        WindowTitleFormat = "리소스 관리자 - {0}",
        TrayTooltipFormat = "리소스 관리자 - {0}",

        AvailabilityPanelName = "로컬 서비스 상태",
        ConnectingTitle = "로컬 서비스에 연결하는 중",
        ReconnectingTitle = "로컬 서비스에 다시 연결하는 중",
        ConnectingDetail = "서비스 ID를 확인하고 세션을 설정하는 중입니다.",
        RetryButton = "다시 시도",
        RetryButtonDescription = "지금 로컬 서비스에 다시 연결합니다",
        DiagnosticsButton = "진단 폴더 열기",
        DiagnosticsButtonDescription = "로컬 진단 파일 폴더를 엽니다",
        ExitButton = "종료",
        ExitButtonDescription = "리소스 관리자를 종료합니다",
        BackendUnavailableTitle = "로컬 서비스를 일시적으로 사용할 수 없습니다",
        BackendUnavailableDetail = "리소스 관리자가 백그라운드에서 계속 연결을 시도합니다.",
        FrontendLoadingTitle = "인터페이스를 로드하는 중",
        FrontendLoadingDetail = "로컬 서비스가 확인되었습니다. 앱 인터페이스를 로드하는 중입니다.",
        FrontendUnavailableTitle = "앱 인터페이스를 일시적으로 사용할 수 없습니다",
        FrontendUnavailableDetail = "로컬 서비스가 계속 실행 중이므로 인터페이스를 다시 로드할 수 있습니다.",

        StatusConnecting = "로컬 서비스에 연결하는 중",
        StatusReconnecting = "로컬 서비스에 다시 연결하는 중",
        StatusBackendUnavailable = "로컬 서비스를 사용할 수 없음",
        StatusShuttingDown = "종료하는 중",
        StatusBackendReady = "로컬 서비스 준비됨",
        StatusFrontendLoading = "인터페이스를 로드하는 중",
        StatusReady = "준비됨",
        StatusFrontendUnavailable = "인터페이스를 사용할 수 없음",
        ActionReconnectBackend = "로컬 서비스에 다시 연결",
        ActionReloadFrontend = "인터페이스 다시 로드",

        BackendProcessExitedFormat = "로컬 서비스 프로세스가 종료 코드 {0}(으)로 종료되었습니다.",
        BackendHealthProbeFailedFormat = "로컬 서비스 상태 검사가 연속 {0}회 실패했습니다.",
        BackendStartFailedFormat = "로컬 서비스를 시작하지 못했습니다: {0}",
        BackendLifecycleMonitorFailedFormat = "로컬 서비스 수명 주기 모니터링에 실패했습니다: {0}",
        BackendHttpErrorFormat = "로컬 서비스가 HTTP {0}을(를) 반환했습니다.",
        BackendEntryMissing = "최종 이미지에 로컬 백엔드 서비스 진입점이 없습니다.",
        BackendNotOwned = "로컬 백엔드 서비스가 준비되지 않았습니다. 네이티브 UI에는 서비스 시작 권한이 없습니다. ResourceManager.Service를 시작하거나 복구하세요.",
        BackendPipelineNotReady = "로컬 서비스 요청 파이프라인이 준비되지 않았습니다.",
        NoProcessToTerminate = "종료할 프로세스가 없습니다.",
        TerminateRequestCompleted = "프로세스 종료 요청이 완료되었습니다.",

        WebViewInitializationFailed = "포함된 인터페이스를 초기화하지 못했습니다. 진단 폴더에서 자세한 내용을 확인할 수 있습니다.",
        FrontendIdentityVerificationFailed = "프런트엔드 자산 ID 확인에 실패했습니다. 진단 폴더에서 자세한 내용을 확인할 수 있습니다.",
        FrontendLoadFailed = "확인된 서비스에서 로컬 인터페이스를 로드하지 못했습니다.",
        WebViewRuntimeMissing = "호환되는 WebView2 런타임을 찾을 수 없습니다. 공유 런타임을 설치한 후 다시 시도하세요.",

        TrayBackendStatusConnecting = "로컬 서비스: 연결 중",
        TrayStatusFormat = "상태: {0}",
        TrayOpen = "열기",
        TrayReconnectBackend = "로컬 서비스에 다시 연결",
        TrayExit = "종료",
        BackendUnavailableBalloonTitle = "로컬 서비스를 사용할 수 없음",
        NoVerifiedSession = "확인 가능한 로컬 서비스 세션이 아직 설정되지 않았습니다.",
        DiagnosticsOpenFailed = "진단 폴더를 열 수 없습니다.",

        SelectSoftwareRootFolder = "앱 루트 폴더 선택",

        ForceTerminateHotkeyBalloonTitle = "강제 종료 단축키",
        NoForegroundProcessToTerminate = "종료할 포그라운드 또는 응답 없는 앱이 없습니다.",
        BackendUnavailableNoTerminate = "로컬 서비스를 사용할 수 없어 프로세스를 종료하지 않았습니다.",
        ForceTerminateFailed = "강제 종료에 실패했습니다. 잠시 후 다시 시도하세요.",
        ForceTerminateHotkeyDisabled = "강제 종료 단축키가 사용 중지되었습니다. Ctrl, Alt 또는 Win 한정 키와 한정 키가 아닌 키를 함께 포함해야 합니다.",
        ForceTerminateHotkeyEnableFailed = "강제 종료 단축키를 사용하도록 설정하지 못했습니다. 키 설정을 확인한 후 다시 시도하세요.",
        HotkeyNeedsAtLeastOneKey = "단축키에는 키가 하나 이상 필요합니다.",
        ForceTerminateHotkeyNeedsModifier = "강제 종료 단축키에는 Ctrl, Alt 또는 Win 한정 키와 한정 키가 아닌 키가 함께 필요합니다.",
        KeyboardHookRegisterFailed = "사용자 지정 전역 키보드 단축키 후크를 등록할 수 없습니다.",
        MouseHookRegisterFailed = "사용자 지정 전역 마우스 단축키 후크를 등록할 수 없습니다.",

        TaskManagerHotkeyReplacement = "작업 관리자 단축키 대체",
        TaskManagerHotkeyEnableFailed = "작업 관리자 단축키를 사용하도록 설정하지 못했습니다. 잠시 후 다시 시도하세요.",
        TaskManagerHotkeyDisableFailed = "작업 관리자 단축키를 사용 중지하지 못했습니다. 잠시 후 다시 시도하세요.",
        TaskManagerHotkeyForeignSetting = "다른 작업 관리자 단축키 설정이 감지되어 기존 설정을 유지했습니다.",
        TaskManagerHotkeyNeedsAdminToEnable = "리소스 관리자가 실행 중이 아닐 때 작업 관리자 단축키를 넘겨받으려면 관리자 권한이 필요합니다.",
        TaskManagerHotkeyNeedsAdminToDisable = "작업 관리자 단축키 대체를 완전히 끄려면 관리자 권한이 필요합니다.",
        TaskManagerHotkeyEnabled = "작업 관리자 단축키가 사용 설정되었습니다.",
        TaskManagerHotkeyDisabled = "작업 관리자 단축키가 사용 중지되었습니다.",
        TaskManagerShortcutHookRegisterFailed = "Ctrl+Shift+Esc 단축키 후크를 등록할 수 없습니다.",
        TaskManagerShortcutHookRemoveFailed = "Ctrl+Shift+Esc 단축키 후크를 제거할 수 없습니다."
    };
}
