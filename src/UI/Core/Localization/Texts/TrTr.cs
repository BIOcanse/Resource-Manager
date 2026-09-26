namespace ResourceManager.NativeUi.Localization.Texts;

internal static class TrTr
{
    public static readonly NativeText Text = new()
    {
        AppName = "Kaynak Yöneticisi",
        WindowTitleFormat = "Kaynak Yöneticisi - {0}",
        TrayTooltipFormat = "Kaynak Yöneticisi - {0}",

        AvailabilityPanelName = "Yerel hizmet durumu",
        ConnectingTitle = "Yerel hizmete bağlanılıyor",
        ReconnectingTitle = "Yerel hizmete yeniden bağlanılıyor",
        ConnectingDetail = "Hizmet kimliği doğrulanıyor ve oturum kuruluyor.",
        RetryButton = "Yeniden dene",
        RetryButtonDescription = "Yerel hizmete hemen yeniden bağlan",
        DiagnosticsButton = "Tanılama klasörünü aç",
        DiagnosticsButtonDescription = "Yerel tanılama dosyaları klasörünü aç",
        ExitButton = "Çıkış",
        ExitButtonDescription = "Kaynak Yöneticisi'nden çık",
        BackendUnavailableTitle = "Yerel hizmet geçici olarak kullanılamıyor",
        BackendUnavailableDetail = "Kaynak Yöneticisi arka planda bağlanmayı denemeye devam ediyor.",
        FrontendLoadingTitle = "Arayüz yükleniyor",
        FrontendLoadingDetail = "Yerel hizmet doğrulandı; uygulama arayüzü yükleniyor.",
        FrontendUnavailableTitle = "Uygulama arayüzü geçici olarak kullanılamıyor",
        FrontendUnavailableDetail = "Yerel hizmet çalışmaya devam ediyor, arayüz yeniden yüklenebilir.",

        StatusConnecting = "Yerel hizmete bağlanılıyor",
        StatusReconnecting = "Yerel hizmete yeniden bağlanılıyor",
        StatusBackendUnavailable = "Yerel hizmet kullanılamıyor",
        StatusShuttingDown = "Çıkılıyor",
        StatusBackendReady = "Yerel hizmet hazır",
        StatusFrontendLoading = "Arayüz yükleniyor",
        StatusReady = "Hazır",
        StatusFrontendUnavailable = "Arayüz kullanılamıyor",
        ActionReconnectBackend = "Yerel hizmete yeniden bağlan",
        ActionReloadFrontend = "Arayüzü yeniden yükle",

        BackendProcessExitedFormat = "Yerel hizmet işlemi {0} koduyla sonlandı.",
        BackendHealthProbeFailedFormat = "Yerel hizmet arka arkaya {0} durum denetiminde başarısız oldu.",
        BackendStartFailedFormat = "Yerel hizmet başlatılamadı: {0}",
        BackendLifecycleMonitorFailedFormat = "Yerel hizmet yaşam döngüsü izleme başarısız oldu: {0}",
        BackendHttpErrorFormat = "Yerel hizmet HTTP {0} döndürdü.",
        BackendEntryMissing = "Son görüntüde yerel arka uç hizmetinin giriş noktası eksik.",
        BackendNotOwned = "Yerel arka uç hizmeti hazır değil; yerel arayüzün hizmeti başlatma yetkisi yok. ResourceManager.Service hizmetini başlatın veya onarın.",
        BackendPipelineNotReady = "Yerel hizmet istek işlem hattı hazır değil.",
        NoProcessToTerminate = "Sonlandırılacak işlem yok.",
        TerminateRequestCompleted = "İşlem sonlandırma isteği tamamlandı.",

        WebViewInitializationFailed = "Gömülü arayüz başlatılamadı. Ayrıntılar için tanılama klasörünü açabilirsiniz.",
        FrontendIdentityVerificationFailed = "Ön uç kaynaklarının kimlik doğrulaması başarısız oldu. Ayrıntılar için tanılama klasörünü açabilirsiniz.",
        FrontendLoadFailed = "Yerel arayüz, doğrulanmış hizmetten yüklenemedi.",
        WebViewRuntimeMissing = "Uyumlu bir WebView2 çalışma zamanı bulunamadı. Paylaşılan çalışma zamanını yükleyip yeniden deneyin.",

        TrayBackendStatusConnecting = "Yerel hizmet: bağlanılıyor",
        TrayStatusFormat = "Durum: {0}",
        TrayOpen = "Aç",
        TrayReconnectBackend = "Yerel hizmete yeniden bağlan",
        TrayExit = "Çıkış",
        BackendUnavailableBalloonTitle = "Yerel hizmet kullanılamıyor",
        NoVerifiedSession = "Doğrulanabilir bir yerel hizmet oturumu henüz kurulmadı.",
        DiagnosticsOpenFailed = "Tanılama klasörü açılamadı.",

        SelectSoftwareRootFolder = "Uygulamanın kök klasörünü seçin",

        ForceTerminateHotkeyBalloonTitle = "Zorla sonlandırma kısayolu",
        NoForegroundProcessToTerminate = "Sonlandırılacak ön plandaki veya yanıt vermeyen bir uygulama yok.",
        BackendUnavailableNoTerminate = "Yerel hizmet kullanılamadığı için hiçbir işlem sonlandırılmadı.",
        ForceTerminateFailed = "Zorla sonlandırma başarısız oldu. Daha sonra yeniden deneyin.",
        ForceTerminateHotkeyDisabled = "Zorla sonlandırma kısayolu devre dışı: Ctrl, Alt veya Win ile birlikte değiştirici olmayan bir tuş içermelidir.",
        ForceTerminateHotkeyEnableFailed = "Zorla sonlandırma kısayolu etkinleştirilemedi. Tuş bileşimini kontrol edip yeniden deneyin.",
        HotkeyNeedsAtLeastOneKey = "Bir kısayol en az bir tuş gerektirir.",
        ForceTerminateHotkeyNeedsModifier = "Zorla sonlandırma kısayolu, Ctrl, Alt veya Win ile birlikte değiştirici olmayan bir tuş içermelidir.",
        KeyboardHookRegisterFailed = "Özel genel klavye kısayol kancası kaydedilemedi.",
        MouseHookRegisterFailed = "Özel genel fare kısayol kancası kaydedilemedi.",

        TaskManagerHotkeyReplacement = "Görev Yöneticisi kısayolu değiştirme",
        TaskManagerHotkeyEnableFailed = "Görev Yöneticisi kısayolu etkinleştirilemedi. Daha sonra yeniden deneyin.",
        TaskManagerHotkeyDisableFailed = "Görev Yöneticisi kısayolu devre dışı bırakılamadı. Daha sonra yeniden deneyin.",
        TaskManagerHotkeyForeignSetting = "Başka bir Görev Yöneticisi kısayol ayarı algılandı ve değiştirilmeden bırakıldı.",
        TaskManagerHotkeyNeedsAdminToEnable = "Kaynak Yöneticisi çalışmıyorken Görev Yöneticisi kısayolunu devralmak için yönetici izni gerekir.",
        TaskManagerHotkeyNeedsAdminToDisable = "Görev Yöneticisi kısayolu değiştirmeyi tamamen kapatmak için yönetici izni gerekir.",
        TaskManagerHotkeyEnabled = "Görev Yöneticisi kısayolu etkin.",
        TaskManagerHotkeyDisabled = "Görev Yöneticisi kısayolu devre dışı.",
        TaskManagerShortcutHookRegisterFailed = "Ctrl+Shift+Esc kısayol kancası kaydedilemedi.",
        TaskManagerShortcutHookRemoveFailed = "Ctrl+Shift+Esc kısayol kancası kaldırılamadı."
    };
}
