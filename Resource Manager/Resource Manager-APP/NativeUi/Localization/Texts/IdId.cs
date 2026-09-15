namespace ResourceManager.NativeUi.Localization.Texts;

internal static class IdId
{
    public static readonly NativeText Text = new()
    {
        AppName = "Pengelola Sumber Daya",
        WindowTitleFormat = "Pengelola Sumber Daya - {0}",
        TrayTooltipFormat = "Pengelola Sumber Daya - {0}",

        AvailabilityPanelName = "Status layanan lokal",
        ConnectingTitle = "Menghubungkan ke layanan lokal",
        ReconnectingTitle = "Menghubungkan ulang ke layanan lokal",
        ConnectingDetail = "Memverifikasi identitas layanan dan membuat sesi.",
        RetryButton = "Coba lagi",
        RetryButtonDescription = "Hubungkan ulang ke layanan lokal sekarang",
        DiagnosticsButton = "Buka folder diagnostik",
        DiagnosticsButtonDescription = "Buka folder berkas diagnostik lokal",
        ExitButton = "Keluar",
        ExitButtonDescription = "Keluar dari Pengelola Sumber Daya",
        BackendUnavailableTitle = "Layanan lokal sementara tidak tersedia",
        BackendUnavailableDetail = "Pengelola Sumber Daya terus mencoba menghubungkan di latar belakang.",
        FrontendLoadingTitle = "Memuat antarmuka",
        FrontendLoadingDetail = "Layanan lokal telah diverifikasi; antarmuka aplikasi sedang dimuat.",
        FrontendUnavailableTitle = "Antarmuka aplikasi sementara tidak tersedia",
        FrontendUnavailableDetail = "Layanan lokal masih berjalan, jadi antarmuka dapat dimuat ulang.",

        StatusConnecting = "Menghubungkan ke layanan lokal",
        StatusReconnecting = "Menghubungkan ulang ke layanan lokal",
        StatusBackendUnavailable = "Layanan lokal tidak tersedia",
        StatusShuttingDown = "Sedang keluar",
        StatusBackendReady = "Layanan lokal siap",
        StatusFrontendLoading = "Memuat antarmuka",
        StatusReady = "Siap",
        StatusFrontendUnavailable = "Antarmuka tidak tersedia",
        ActionReconnectBackend = "Hubungkan ulang ke layanan lokal",
        ActionReloadFrontend = "Muat ulang antarmuka",

        BackendProcessExitedFormat = "Proses layanan lokal keluar dengan kode {0}.",
        BackendHealthProbeFailedFormat = "Layanan lokal gagal dalam {0} pemeriksaan kesehatan berturut-turut.",
        BackendStartFailedFormat = "Layanan lokal gagal dijalankan: {0}",
        BackendLifecycleMonitorFailedFormat = "Pemantauan siklus hidup layanan lokal gagal: {0}",
        BackendHttpErrorFormat = "Layanan lokal mengembalikan HTTP {0}.",
        BackendEntryMissing = "Titik masuk layanan backend lokal tidak ada dalam image akhir.",
        BackendNotOwned = "Layanan backend lokal belum siap; antarmuka native tidak memiliki hak untuk menjalankan layanan. Jalankan atau perbaiki ResourceManager.Service.",
        BackendPipelineNotReady = "Alur permintaan layanan lokal belum siap.",
        NoProcessToTerminate = "Tidak ada proses yang perlu dihentikan.",
        TerminateRequestCompleted = "Permintaan penghentian proses telah selesai.",

        WebViewInitializationFailed = "Antarmuka tersemat gagal diinisialisasi. Buka folder diagnostik untuk melihat detailnya.",
        FrontendIdentityVerificationFailed = "Verifikasi identitas aset frontend gagal. Buka folder diagnostik untuk melihat detailnya.",
        FrontendLoadFailed = "Antarmuka lokal tidak dapat dimuat dari layanan yang terverifikasi.",
        WebViewRuntimeMissing = "Runtime WebView2 yang kompatibel tidak ditemukan. Instal runtime bersama lalu coba lagi.",

        TrayBackendStatusConnecting = "Layanan lokal: menghubungkan",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Buka",
        TrayReconnectBackend = "Hubungkan ulang ke layanan lokal",
        TrayExit = "Keluar",
        BackendUnavailableBalloonTitle = "Layanan lokal tidak tersedia",
        NoVerifiedSession = "Belum ada sesi layanan lokal yang dapat diverifikasi.",
        DiagnosticsOpenFailed = "Folder diagnostik tidak dapat dibuka.",

        SelectSoftwareRootFolder = "Pilih folder akar aplikasi",

        ForceTerminateHotkeyBalloonTitle = "Pintasan penghentian paksa",
        NoForegroundProcessToTerminate = "Tidak ada aplikasi latar depan atau yang tidak merespons untuk dihentikan.",
        BackendUnavailableNoTerminate = "Layanan lokal tidak tersedia, sehingga tidak ada proses yang dihentikan.",
        ForceTerminateFailed = "Penghentian paksa gagal. Coba lagi nanti.",
        ForceTerminateHotkeyDisabled = "Pintasan penghentian paksa dinonaktifkan: pintasan harus menggabungkan Ctrl, Alt, atau Win dengan tombol non-pengubah.",
        ForceTerminateHotkeyEnableFailed = "Pintasan penghentian paksa tidak dapat diaktifkan. Periksa kombinasi tombol lalu coba lagi.",
        HotkeyNeedsAtLeastOneKey = "Pintasan memerlukan setidaknya satu tombol.",
        ForceTerminateHotkeyNeedsModifier = "Pintasan penghentian paksa harus menggabungkan Ctrl, Alt, atau Win dengan tombol non-pengubah.",
        KeyboardHookRegisterFailed = "Hook keyboard global kustom tidak dapat didaftarkan.",
        MouseHookRegisterFailed = "Hook mouse global kustom tidak dapat didaftarkan.",

        TaskManagerHotkeyReplacement = "Penggantian pintasan Task Manager",
        TaskManagerHotkeyEnableFailed = "Pintasan Task Manager tidak dapat diaktifkan. Coba lagi nanti.",
        TaskManagerHotkeyDisableFailed = "Pintasan Task Manager tidak dapat dinonaktifkan. Coba lagi nanti.",
        TaskManagerHotkeyForeignSetting = "Pengaturan pintasan Task Manager lain terdeteksi dan dibiarkan tidak berubah.",
        TaskManagerHotkeyNeedsAdminToEnable = "Izin administrator diperlukan untuk mengambil alih pintasan Task Manager saat Pengelola Sumber Daya tidak berjalan.",
        TaskManagerHotkeyNeedsAdminToDisable = "Izin administrator diperlukan untuk menonaktifkan penggantian pintasan Task Manager sepenuhnya.",
        TaskManagerHotkeyEnabled = "Pintasan Task Manager aktif.",
        TaskManagerHotkeyDisabled = "Pintasan Task Manager nonaktif.",
        TaskManagerShortcutHookRegisterFailed = "Hook pintasan Ctrl+Shift+Esc tidak dapat didaftarkan.",
        TaskManagerShortcutHookRemoveFailed = "Hook pintasan Ctrl+Shift+Esc tidak dapat dihapus."
    };
}
