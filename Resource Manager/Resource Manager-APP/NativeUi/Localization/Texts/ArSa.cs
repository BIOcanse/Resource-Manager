namespace ResourceManager.NativeUi.Localization.Texts;

internal static class ArSa
{
    public static readonly NativeText Text = new()
    {
        AppName = "مدير الموارد",
        WindowTitleFormat = "مدير الموارد - {0}",
        TrayTooltipFormat = "مدير الموارد - {0}",

        AvailabilityPanelName = "حالة الخدمة المحلية",
        ConnectingTitle = "جارٍ الاتصال بالخدمة المحلية",
        ReconnectingTitle = "جارٍ إعادة الاتصال بالخدمة المحلية",
        ConnectingDetail = "جارٍ التحقق من هوية الخدمة وإنشاء الجلسة.",
        RetryButton = "إعادة المحاولة",
        RetryButtonDescription = "إعادة الاتصال بالخدمة المحلية الآن",
        DiagnosticsButton = "فتح مجلد التشخيص",
        DiagnosticsButtonDescription = "فتح مجلد ملفات التشخيص المحلية",
        ExitButton = "خروج",
        ExitButtonDescription = "الخروج من مدير الموارد",
        BackendUnavailableTitle = "الخدمة المحلية غير متوفرة مؤقتًا",
        BackendUnavailableDetail = "يواصل مدير الموارد محاولة الاتصال في الخلفية.",
        FrontendLoadingTitle = "جارٍ تحميل الواجهة",
        FrontendLoadingDetail = "تم التحقق من الخدمة المحلية، ويجري تحميل واجهة التطبيق.",
        FrontendUnavailableTitle = "واجهة التطبيق غير متوفرة مؤقتًا",
        FrontendUnavailableDetail = "لا تزال الخدمة المحلية قيد التشغيل، لذا يمكن إعادة تحميل الواجهة.",

        StatusConnecting = "جارٍ الاتصال بالخدمة المحلية",
        StatusReconnecting = "جارٍ إعادة الاتصال بالخدمة المحلية",
        StatusBackendUnavailable = "الخدمة المحلية غير متوفرة",
        StatusShuttingDown = "جارٍ الخروج",
        StatusBackendReady = "الخدمة المحلية جاهزة",
        StatusFrontendLoading = "جارٍ تحميل الواجهة",
        StatusReady = "جاهز",
        StatusFrontendUnavailable = "الواجهة غير متوفرة",
        ActionReconnectBackend = "إعادة الاتصال بالخدمة المحلية",
        ActionReloadFrontend = "إعادة تحميل الواجهة",

        BackendProcessExitedFormat = "انتهت عملية الخدمة المحلية برمز الخروج {0}.",
        BackendHealthProbeFailedFormat = "فشلت الخدمة المحلية في {0} من فحوص السلامة المتتالية.",
        BackendStartFailedFormat = "تعذّر بدء الخدمة المحلية: {0}",
        BackendLifecycleMonitorFailedFormat = "فشلت مراقبة دورة حياة الخدمة المحلية: {0}",
        BackendHttpErrorFormat = "أعادت الخدمة المحلية HTTP {0}.",
        BackendEntryMissing = "نقطة دخول خدمة الواجهة الخلفية المحلية مفقودة من الصورة النهائية.",
        BackendNotOwned = "خدمة الواجهة الخلفية المحلية غير جاهزة؛ الواجهة الأصلية لا تملك صلاحية بدء الخدمة. ابدأ خدمة ResourceManager.Service أو أصلحها.",
        BackendPipelineNotReady = "مسار طلبات الخدمة المحلية غير جاهز.",
        NoProcessToTerminate = "لا توجد عملية لإنهائها.",
        TerminateRequestCompleted = "اكتمل طلب إنهاء العملية.",

        WebViewInitializationFailed = "تعذّر تهيئة الواجهة المضمّنة. افتح مجلد التشخيص للاطلاع على التفاصيل.",
        FrontendIdentityVerificationFailed = "فشل التحقق من هوية موارد الواجهة الأمامية. افتح مجلد التشخيص للاطلاع على التفاصيل.",
        FrontendLoadFailed = "تعذّر تحميل الواجهة المحلية من الخدمة التي تم التحقق منها.",
        WebViewRuntimeMissing = "لم يتم العثور على وقت تشغيل WebView2 متوافق. ثبّت وقت التشغيل المشترك ثم أعد المحاولة.",

        TrayBackendStatusConnecting = "الخدمة المحلية: جارٍ الاتصال",
        TrayStatusFormat = "الحالة: {0}",
        TrayOpen = "فتح",
        TrayReconnectBackend = "إعادة الاتصال بالخدمة المحلية",
        TrayExit = "خروج",
        BackendUnavailableBalloonTitle = "الخدمة المحلية غير متوفرة",
        NoVerifiedSession = "لم يتم بعد إنشاء جلسة قابلة للتحقق مع الخدمة المحلية.",
        DiagnosticsOpenFailed = "تعذّر فتح مجلد التشخيص.",

        SelectSoftwareRootFolder = "اختر المجلد الجذر للتطبيق",

        ForceTerminateHotkeyBalloonTitle = "اختصار الإنهاء القسري",
        NoForegroundProcessToTerminate = "لا يوجد تطبيق في المقدمة أو تطبيق غير مستجيب لإنهائه.",
        BackendUnavailableNoTerminate = "الخدمة المحلية غير متوفرة، لذلك لم يتم إنهاء أي عملية.",
        ForceTerminateFailed = "فشل الإنهاء القسري. أعد المحاولة لاحقًا.",
        ForceTerminateHotkeyDisabled = "اختصار الإنهاء القسري معطّل: يجب أن يجمع بين Ctrl أو Alt أو Win ومفتاح غير معدِّل.",
        ForceTerminateHotkeyEnableFailed = "تعذّر تمكين اختصار الإنهاء القسري. تحقق من تركيبة المفاتيح ثم أعد المحاولة.",
        HotkeyNeedsAtLeastOneKey = "يتطلب الاختصار مفتاحًا واحدًا على الأقل.",
        ForceTerminateHotkeyNeedsModifier = "يجب أن يجمع اختصار الإنهاء القسري بين Ctrl أو Alt أو Win ومفتاح غير معدِّل.",
        KeyboardHookRegisterFailed = "تعذّر تسجيل خطاف لوحة المفاتيح العام المخصص.",
        MouseHookRegisterFailed = "تعذّر تسجيل خطاف الماوس العام المخصص.",

        TaskManagerHotkeyReplacement = "استبدال اختصار إدارة المهام",
        TaskManagerHotkeyEnableFailed = "تعذّر تمكين اختصار إدارة المهام. أعد المحاولة لاحقًا.",
        TaskManagerHotkeyDisableFailed = "تعذّر تعطيل اختصار إدارة المهام. أعد المحاولة لاحقًا.",
        TaskManagerHotkeyForeignSetting = "تم اكتشاف إعداد آخر لاختصار إدارة المهام وتم الإبقاء عليه دون تغيير.",
        TaskManagerHotkeyNeedsAdminToEnable = "يلزم إذن المسؤول للاستحواذ على اختصار إدارة المهام عندما لا يكون مدير الموارد قيد التشغيل.",
        TaskManagerHotkeyNeedsAdminToDisable = "يلزم إذن المسؤول لإيقاف استبدال اختصار إدارة المهام بالكامل.",
        TaskManagerHotkeyEnabled = "تم تمكين اختصار إدارة المهام.",
        TaskManagerHotkeyDisabled = "تم تعطيل اختصار إدارة المهام.",
        TaskManagerShortcutHookRegisterFailed = "تعذّر تسجيل خطاف اختصار Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "تعذّرت إزالة خطاف اختصار Ctrl+Shift+Esc."
    };
}
