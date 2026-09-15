namespace ResourceManager.NativeUi.Localization.Texts;

internal static class CsCz
{
    public static readonly NativeText Text = new()
    {
        AppName = "Správce prostředků",
        WindowTitleFormat = "Správce prostředků - {0}",
        TrayTooltipFormat = "Správce prostředků - {0}",

        AvailabilityPanelName = "Stav místní služby",
        ConnectingTitle = "Připojování k místní službě",
        ReconnectingTitle = "Opětovné připojování k místní službě",
        ConnectingDetail = "Probíhá ověření identity služby a navázání relace.",
        RetryButton = "Zkusit znovu",
        RetryButtonDescription = "Znovu se připojit k místní službě",
        DiagnosticsButton = "Otevřít složku diagnostiky",
        DiagnosticsButtonDescription = "Otevřít složku s místními diagnostickými soubory",
        ExitButton = "Ukončit",
        ExitButtonDescription = "Ukončit Správce prostředků",
        BackendUnavailableTitle = "Místní služba je dočasně nedostupná",
        BackendUnavailableDetail = "Správce prostředků se dál pokouší připojit na pozadí.",
        FrontendLoadingTitle = "Načítání rozhraní",
        FrontendLoadingDetail = "Místní služba je ověřena, načítá se rozhraní aplikace.",
        FrontendUnavailableTitle = "Rozhraní aplikace je dočasně nedostupné",
        FrontendUnavailableDetail = "Místní služba stále běží, takže rozhraní lze načíst znovu.",

        StatusConnecting = "Připojování k místní službě",
        StatusReconnecting = "Opětovné připojování k místní službě",
        StatusBackendUnavailable = "Místní služba není dostupná",
        StatusShuttingDown = "Ukončování",
        StatusBackendReady = "Místní služba je připravena",
        StatusFrontendLoading = "Načítání rozhraní",
        StatusReady = "Připraveno",
        StatusFrontendUnavailable = "Rozhraní není dostupné",
        ActionReconnectBackend = "Znovu se připojit k místní službě",
        ActionReloadFrontend = "Znovu načíst rozhraní",

        BackendProcessExitedFormat = "Proces místní služby skončil s kódem {0}.",
        BackendHealthProbeFailedFormat = "Místní služba neprošla {0} kontrolami stavu za sebou.",
        BackendStartFailedFormat = "Místní službu se nepodařilo spustit: {0}",
        BackendLifecycleMonitorFailedFormat = "Sledování životního cyklu místní služby selhalo: {0}",
        BackendHttpErrorFormat = "Místní služba vrátila HTTP {0}.",
        BackendEntryMissing = "V konečném obrazu chybí vstupní bod místní serverové služby.",
        BackendNotOwned = "Místní serverová služba není připravena; nativní rozhraní nemá oprávnění ji spustit. Spusťte nebo opravte ResourceManager.Service.",
        BackendPipelineNotReady = "Kanál požadavků místní služby není připraven.",
        NoProcessToTerminate = "Není žádný proces k ukončení.",
        TerminateRequestCompleted = "Požadavek na ukončení procesu byl dokončen.",

        WebViewInitializationFailed = "Vložené rozhraní se nepodařilo inicializovat. Podrobnosti najdete ve složce diagnostiky.",
        FrontendIdentityVerificationFailed = "Ověření identity prostředků frontendu selhalo. Podrobnosti najdete ve složce diagnostiky.",
        FrontendLoadFailed = "Místní rozhraní se nepodařilo načíst z ověřené služby.",
        WebViewRuntimeMissing = "Nebyl nalezen kompatibilní běhový modul WebView2. Nainstalujte sdílený běhový modul a zkuste to znovu.",

        TrayBackendStatusConnecting = "Místní služba: připojování",
        TrayStatusFormat = "Stav: {0}",
        TrayOpen = "Otevřít",
        TrayReconnectBackend = "Znovu se připojit k místní službě",
        TrayExit = "Ukončit",
        BackendUnavailableBalloonTitle = "Místní služba není dostupná",
        NoVerifiedSession = "Ověřitelná relace s místní službou zatím nebyla navázána.",
        DiagnosticsOpenFailed = "Složku diagnostiky se nepodařilo otevřít.",

        SelectSoftwareRootFolder = "Vyberte kořenovou složku aplikace",

        ForceTerminateHotkeyBalloonTitle = "Klávesová zkratka vynuceného ukončení",
        NoForegroundProcessToTerminate = "Není žádná aplikace v popředí ani nereagující aplikace k ukončení.",
        BackendUnavailableNoTerminate = "Místní služba není dostupná, žádný proces nebyl ukončen.",
        ForceTerminateFailed = "Vynucené ukončení selhalo. Zkuste to později.",
        ForceTerminateHotkeyDisabled = "Klávesová zkratka vynuceného ukončení je vypnutá: musí kombinovat Ctrl, Alt nebo Win s klávesou, která není modifikátor.",
        ForceTerminateHotkeyEnableFailed = "Klávesovou zkratku vynuceného ukončení se nepodařilo zapnout. Zkontrolujte kombinaci kláves a zkuste to znovu.",
        HotkeyNeedsAtLeastOneKey = "Klávesová zkratka vyžaduje alespoň jednu klávesu.",
        ForceTerminateHotkeyNeedsModifier = "Klávesová zkratka vynuceného ukončení musí kombinovat Ctrl, Alt nebo Win s klávesou, která není modifikátor.",
        KeyboardHookRegisterFailed = "Vlastní globální hák klávesnice se nepodařilo zaregistrovat.",
        MouseHookRegisterFailed = "Vlastní globální hák myši se nepodařilo zaregistrovat.",

        TaskManagerHotkeyReplacement = "Nahrazení klávesové zkratky Správce úloh",
        TaskManagerHotkeyEnableFailed = "Klávesovou zkratku Správce úloh se nepodařilo zapnout. Zkuste to později.",
        TaskManagerHotkeyDisableFailed = "Klávesovou zkratku Správce úloh se nepodařilo vypnout. Zkuste to později.",
        TaskManagerHotkeyForeignSetting = "Bylo zjištěno jiné nastavení klávesové zkratky Správce úloh a zůstalo beze změny.",
        TaskManagerHotkeyNeedsAdminToEnable = "K převzetí klávesové zkratky Správce úloh, když Správce prostředků neběží, jsou potřeba oprávnění správce.",
        TaskManagerHotkeyNeedsAdminToDisable = "K úplnému vypnutí nahrazení klávesové zkratky Správce úloh jsou potřeba oprávnění správce.",
        TaskManagerHotkeyEnabled = "Klávesová zkratka Správce úloh je zapnutá.",
        TaskManagerHotkeyDisabled = "Klávesová zkratka Správce úloh je vypnutá.",
        TaskManagerShortcutHookRegisterFailed = "Hák klávesové zkratky Ctrl+Shift+Esc se nepodařilo zaregistrovat.",
        TaskManagerShortcutHookRemoveFailed = "Hák klávesové zkratky Ctrl+Shift+Esc se nepodařilo odebrat."
    };
}
