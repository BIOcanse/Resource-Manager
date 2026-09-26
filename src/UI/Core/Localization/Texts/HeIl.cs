namespace ResourceManager.NativeUi.Localization.Texts;

internal static class HeIl
{
    public static readonly NativeText Text = new()
    {
        AppName = "מנהל המשאבים",
        WindowTitleFormat = "מנהל המשאבים - {0}",
        TrayTooltipFormat = "מנהל המשאבים - {0}",

        AvailabilityPanelName = "מצב השירות המקומי",
        ConnectingTitle = "מתחבר לשירות המקומי",
        ReconnectingTitle = "מתחבר מחדש לשירות המקומי",
        ConnectingDetail = "מאמת את זהות השירות ויוצר הפעלה.",
        RetryButton = "נסה שוב",
        RetryButtonDescription = "התחבר עכשיו מחדש לשירות המקומי",
        DiagnosticsButton = "פתח את תיקיית האבחון",
        DiagnosticsButtonDescription = "פתח את תיקיית קובצי האבחון המקומיים",
        ExitButton = "יציאה",
        ExitButtonDescription = "צא ממנהל המשאבים",
        BackendUnavailableTitle = "השירות המקומי אינו זמין באופן זמני",
        BackendUnavailableDetail = "מנהל המשאבים ממשיך לנסות להתחבר ברקע.",
        FrontendLoadingTitle = "טוען את הממשק",
        FrontendLoadingDetail = "השירות המקומי אומת; ממשק האפליקציה נטען.",
        FrontendUnavailableTitle = "ממשק האפליקציה אינו זמין באופן זמני",
        FrontendUnavailableDetail = "השירות המקומי עדיין פועל, ולכן אפשר לטעון את הממשק מחדש.",

        StatusConnecting = "מתחבר לשירות המקומי",
        StatusReconnecting = "מתחבר מחדש לשירות המקומי",
        StatusBackendUnavailable = "השירות המקומי אינו זמין",
        StatusShuttingDown = "יוצא",
        StatusBackendReady = "השירות המקומי מוכן",
        StatusFrontendLoading = "טוען את הממשק",
        StatusReady = "מוכן",
        StatusFrontendUnavailable = "הממשק אינו זמין",
        ActionReconnectBackend = "התחבר מחדש לשירות המקומי",
        ActionReloadFrontend = "טען מחדש את הממשק",

        BackendProcessExitedFormat = "תהליך השירות המקומי הסתיים עם קוד {0}.",
        BackendHealthProbeFailedFormat = "השירות המקומי נכשל ב-{0} בדיקות תקינות רצופות.",
        BackendStartFailedFormat = "הפעלת השירות המקומי נכשלה: {0}",
        BackendLifecycleMonitorFailedFormat = "ניטור מחזור החיים של השירות המקומי נכשל: {0}",
        BackendHttpErrorFormat = "השירות המקומי החזיר HTTP {0}.",
        BackendEntryMissing = "נקודת הכניסה של שירות הקצה האחורי המקומי חסרה בתמונה הסופית.",
        BackendNotOwned = "שירות הקצה האחורי המקומי אינו מוכן; לממשק המקורי אין הרשאה להפעיל את השירות. הפעל או תקן את ResourceManager.Service.",
        BackendPipelineNotReady = "צינור הבקשות של השירות המקומי אינו מוכן.",
        NoProcessToTerminate = "אין תהליך לסיום.",
        TerminateRequestCompleted = "הבקשה לסיום התהליך הושלמה.",

        WebViewInitializationFailed = "אתחול הממשק המוטמע נכשל. פתח את תיקיית האבחון לפרטים.",
        FrontendIdentityVerificationFailed = "אימות הזהות של משאבי הממשק נכשל. פתח את תיקיית האבחון לפרטים.",
        FrontendLoadFailed = "לא ניתן היה לטעון את הממשק המקומי מהשירות המאומת.",
        WebViewRuntimeMissing = "לא נמצאה סביבת ריצה תואמת של WebView2. התקן את סביבת הריצה המשותפת ונסה שוב.",

        TrayBackendStatusConnecting = "שירות מקומי: מתחבר",
        TrayStatusFormat = "מצב: {0}",
        TrayOpen = "פתח",
        TrayReconnectBackend = "התחבר מחדש לשירות המקומי",
        TrayExit = "יציאה",
        BackendUnavailableBalloonTitle = "השירות המקומי אינו זמין",
        NoVerifiedSession = "עדיין לא נוצרה הפעלה ניתנת לאימות מול השירות המקומי.",
        DiagnosticsOpenFailed = "לא ניתן היה לפתוח את תיקיית האבחון.",

        SelectSoftwareRootFolder = "בחר את תיקיית השורש של האפליקציה",

        ForceTerminateHotkeyBalloonTitle = "מקש קיצור לסיום מאולץ",
        NoForegroundProcessToTerminate = "אין אפליקציה בחזית או אפליקציה שאינה מגיבה לסיום.",
        BackendUnavailableNoTerminate = "השירות המקומי אינו זמין, ולכן אף תהליך לא הסתיים.",
        ForceTerminateFailed = "הסיום המאולץ נכשל. נסה שוב מאוחר יותר.",
        ForceTerminateHotkeyDisabled = "מקש הקיצור לסיום מאולץ מושבת: עליו לשלב Ctrl,‏ Alt או Win יחד עם מקש שאינו מקש החלפה.",
        ForceTerminateHotkeyEnableFailed = "לא ניתן היה להפעיל את מקש הקיצור לסיום מאולץ. בדוק את צירוף המקשים ונסה שוב.",
        HotkeyNeedsAtLeastOneKey = "מקש קיצור דורש מקש אחד לפחות.",
        ForceTerminateHotkeyNeedsModifier = "מקש הקיצור לסיום מאולץ חייב לשלב Ctrl,‏ Alt או Win יחד עם מקש שאינו מקש החלפה.",
        KeyboardHookRegisterFailed = "לא ניתן היה לרשום את וו המקלדת הגלובלי המותאם אישית.",
        MouseHookRegisterFailed = "לא ניתן היה לרשום את וו העכבר הגלובלי המותאם אישית.",

        TaskManagerHotkeyReplacement = "החלפת מקש הקיצור של מנהל המשימות",
        TaskManagerHotkeyEnableFailed = "לא ניתן היה להפעיל את מקש הקיצור של מנהל המשימות. נסה שוב מאוחר יותר.",
        TaskManagerHotkeyDisableFailed = "לא ניתן היה להשבית את מקש הקיצור של מנהל המשימות. נסה שוב מאוחר יותר.",
        TaskManagerHotkeyForeignSetting = "זוהתה הגדרה אחרת של מקש הקיצור של מנהל המשימות, והיא נשמרה ללא שינוי.",
        TaskManagerHotkeyNeedsAdminToEnable = "נדרשות הרשאות מנהל כדי להשתלט על מקש הקיצור של מנהל המשימות כשמנהל המשאבים אינו פועל.",
        TaskManagerHotkeyNeedsAdminToDisable = "נדרשות הרשאות מנהל כדי לכבות לחלוטין את החלפת מקש הקיצור של מנהל המשימות.",
        TaskManagerHotkeyEnabled = "מקש הקיצור של מנהל המשימות מופעל.",
        TaskManagerHotkeyDisabled = "מקש הקיצור של מנהל המשימות מושבת.",
        TaskManagerShortcutHookRegisterFailed = "לא ניתן היה לרשום את הוו של מקש הקיצור Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "לא ניתן היה להסיר את הוו של מקש הקיצור Ctrl+Shift+Esc."
    };
}
