namespace ResourceManager.NativeUi.Localization.Texts;

internal static class ViVn
{
    public static readonly NativeText Text = new()
    {
        AppName = "Trình quản lý tài nguyên",
        WindowTitleFormat = "Trình quản lý tài nguyên - {0}",
        TrayTooltipFormat = "Trình quản lý tài nguyên - {0}",

        AvailabilityPanelName = "Trạng thái dịch vụ cục bộ",
        ConnectingTitle = "Đang kết nối tới dịch vụ cục bộ",
        ReconnectingTitle = "Đang kết nối lại tới dịch vụ cục bộ",
        ConnectingDetail = "Đang xác minh danh tính dịch vụ và thiết lập phiên.",
        RetryButton = "Thử lại",
        RetryButtonDescription = "Kết nối lại ngay tới dịch vụ cục bộ",
        DiagnosticsButton = "Mở thư mục chẩn đoán",
        DiagnosticsButtonDescription = "Mở thư mục tệp chẩn đoán cục bộ",
        ExitButton = "Thoát",
        ExitButtonDescription = "Thoát Trình quản lý tài nguyên",
        BackendUnavailableTitle = "Dịch vụ cục bộ tạm thời không khả dụng",
        BackendUnavailableDetail = "Trình quản lý tài nguyên vẫn tiếp tục thử kết nối ở chế độ nền.",
        FrontendLoadingTitle = "Đang tải giao diện",
        FrontendLoadingDetail = "Dịch vụ cục bộ đã được xác minh; đang tải giao diện ứng dụng.",
        FrontendUnavailableTitle = "Giao diện ứng dụng tạm thời không khả dụng",
        FrontendUnavailableDetail = "Dịch vụ cục bộ vẫn đang chạy nên có thể tải lại giao diện.",

        StatusConnecting = "Đang kết nối tới dịch vụ cục bộ",
        StatusReconnecting = "Đang kết nối lại tới dịch vụ cục bộ",
        StatusBackendUnavailable = "Dịch vụ cục bộ không khả dụng",
        StatusShuttingDown = "Đang thoát",
        StatusBackendReady = "Dịch vụ cục bộ đã sẵn sàng",
        StatusFrontendLoading = "Đang tải giao diện",
        StatusReady = "Sẵn sàng",
        StatusFrontendUnavailable = "Giao diện không khả dụng",
        ActionReconnectBackend = "Kết nối lại tới dịch vụ cục bộ",
        ActionReloadFrontend = "Tải lại giao diện",

        BackendProcessExitedFormat = "Tiến trình dịch vụ cục bộ đã thoát với mã {0}.",
        BackendHealthProbeFailedFormat = "Dịch vụ cục bộ đã thất bại {0} lần kiểm tra tình trạng liên tiếp.",
        BackendStartFailedFormat = "Không thể khởi động dịch vụ cục bộ: {0}",
        BackendLifecycleMonitorFailedFormat = "Việc giám sát vòng đời dịch vụ cục bộ đã thất bại: {0}",
        BackendHttpErrorFormat = "Dịch vụ cục bộ trả về HTTP {0}.",
        BackendEntryMissing = "Ảnh cuối cùng thiếu điểm vào của dịch vụ nền cục bộ.",
        BackendNotOwned = "Dịch vụ nền cục bộ chưa sẵn sàng; giao diện gốc không có quyền khởi động dịch vụ. Hãy khởi động hoặc sửa chữa ResourceManager.Service.",
        BackendPipelineNotReady = "Đường ống yêu cầu của dịch vụ cục bộ chưa sẵn sàng.",
        NoProcessToTerminate = "Không có tiến trình nào để kết thúc.",
        TerminateRequestCompleted = "Yêu cầu kết thúc tiến trình đã hoàn tất.",

        WebViewInitializationFailed = "Không thể khởi tạo giao diện nhúng. Hãy mở thư mục chẩn đoán để xem chi tiết.",
        FrontendIdentityVerificationFailed = "Việc xác minh danh tính tài nguyên giao diện đã thất bại. Hãy mở thư mục chẩn đoán để xem chi tiết.",
        FrontendLoadFailed = "Không thể tải giao diện cục bộ từ dịch vụ đã xác minh.",
        WebViewRuntimeMissing = "Không tìm thấy WebView2 Runtime tương thích. Hãy cài đặt runtime dùng chung rồi thử lại.",

        TrayBackendStatusConnecting = "Dịch vụ cục bộ: đang kết nối",
        TrayStatusFormat = "Trạng thái: {0}",
        TrayOpen = "Mở",
        TrayReconnectBackend = "Kết nối lại tới dịch vụ cục bộ",
        TrayExit = "Thoát",
        BackendUnavailableBalloonTitle = "Dịch vụ cục bộ không khả dụng",
        NoVerifiedSession = "Chưa thiết lập được phiên có thể xác minh với dịch vụ cục bộ.",
        DiagnosticsOpenFailed = "Không thể mở thư mục chẩn đoán.",

        SelectSoftwareRootFolder = "Chọn thư mục gốc của ứng dụng",

        ForceTerminateHotkeyBalloonTitle = "Phím tắt kết thúc bắt buộc",
        NoForegroundProcessToTerminate = "Không có ứng dụng nền trước hoặc ứng dụng không phản hồi nào để kết thúc.",
        BackendUnavailableNoTerminate = "Dịch vụ cục bộ không khả dụng nên không có tiến trình nào bị kết thúc.",
        ForceTerminateFailed = "Kết thúc bắt buộc đã thất bại. Hãy thử lại sau.",
        ForceTerminateHotkeyDisabled = "Phím tắt kết thúc bắt buộc đã bị tắt: phím tắt phải kết hợp Ctrl, Alt hoặc Win với một phím không phải phím bổ trợ.",
        ForceTerminateHotkeyEnableFailed = "Không thể bật phím tắt kết thúc bắt buộc. Hãy kiểm tra tổ hợp phím rồi thử lại.",
        HotkeyNeedsAtLeastOneKey = "Phím tắt cần ít nhất một phím.",
        ForceTerminateHotkeyNeedsModifier = "Phím tắt kết thúc bắt buộc phải kết hợp Ctrl, Alt hoặc Win với một phím không phải phím bổ trợ.",
        KeyboardHookRegisterFailed = "Không thể đăng ký hook bàn phím toàn cục tùy chỉnh.",
        MouseHookRegisterFailed = "Không thể đăng ký hook chuột toàn cục tùy chỉnh.",

        TaskManagerHotkeyReplacement = "Thay thế phím tắt Trình quản lý Tác vụ",
        TaskManagerHotkeyEnableFailed = "Không thể bật phím tắt Trình quản lý Tác vụ. Hãy thử lại sau.",
        TaskManagerHotkeyDisableFailed = "Không thể tắt phím tắt Trình quản lý Tác vụ. Hãy thử lại sau.",
        TaskManagerHotkeyForeignSetting = "Đã phát hiện một thiết lập phím tắt Trình quản lý Tác vụ khác và giữ nguyên thiết lập đó.",
        TaskManagerHotkeyNeedsAdminToEnable = "Cần quyền quản trị viên để tiếp quản phím tắt Trình quản lý Tác vụ khi Trình quản lý tài nguyên không chạy.",
        TaskManagerHotkeyNeedsAdminToDisable = "Cần quyền quản trị viên để tắt hoàn toàn việc thay thế phím tắt Trình quản lý Tác vụ.",
        TaskManagerHotkeyEnabled = "Phím tắt Trình quản lý Tác vụ đã được bật.",
        TaskManagerHotkeyDisabled = "Phím tắt Trình quản lý Tác vụ đã được tắt.",
        TaskManagerShortcutHookRegisterFailed = "Không thể đăng ký hook phím tắt Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Không thể gỡ hook phím tắt Ctrl+Shift+Esc."
    };
}
