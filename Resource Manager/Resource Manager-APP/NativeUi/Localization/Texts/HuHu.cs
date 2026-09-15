namespace ResourceManager.NativeUi.Localization.Texts;

internal static class HuHu
{
    public static readonly NativeText Text = new()
    {
        AppName = "Erőforrás-kezelő",
        WindowTitleFormat = "Erőforrás-kezelő - {0}",
        TrayTooltipFormat = "Erőforrás-kezelő - {0}",

        AvailabilityPanelName = "A helyi szolgáltatás állapota",
        ConnectingTitle = "Csatlakozás a helyi szolgáltatáshoz",
        ReconnectingTitle = "Újracsatlakozás a helyi szolgáltatáshoz",
        ConnectingDetail = "A szolgáltatás azonosságának ellenőrzése és a munkamenet létrehozása folyamatban van.",
        RetryButton = "Újra",
        RetryButtonDescription = "Azonnali újracsatlakozás a helyi szolgáltatáshoz",
        DiagnosticsButton = "Diagnosztikai mappa megnyitása",
        DiagnosticsButtonDescription = "A helyi diagnosztikai fájlok mappájának megnyitása",
        ExitButton = "Kilépés",
        ExitButtonDescription = "Kilépés az Erőforrás-kezelőből",
        BackendUnavailableTitle = "A helyi szolgáltatás átmenetileg nem érhető el",
        BackendUnavailableDetail = "Az Erőforrás-kezelő a háttérben tovább próbálkozik a csatlakozással.",
        FrontendLoadingTitle = "A felület betöltése",
        FrontendLoadingDetail = "A helyi szolgáltatás ellenőrizve; az alkalmazás felülete betöltődik.",
        FrontendUnavailableTitle = "Az alkalmazás felülete átmenetileg nem érhető el",
        FrontendUnavailableDetail = "A helyi szolgáltatás továbbra is fut, így a felület újratölthető.",

        StatusConnecting = "Csatlakozás a helyi szolgáltatáshoz",
        StatusReconnecting = "Újracsatlakozás a helyi szolgáltatáshoz",
        StatusBackendUnavailable = "A helyi szolgáltatás nem érhető el",
        StatusShuttingDown = "Kilépés",
        StatusBackendReady = "A helyi szolgáltatás készen áll",
        StatusFrontendLoading = "A felület betöltése",
        StatusReady = "Kész",
        StatusFrontendUnavailable = "A felület nem érhető el",
        ActionReconnectBackend = "Újracsatlakozás a helyi szolgáltatáshoz",
        ActionReloadFrontend = "A felület újratöltése",

        BackendProcessExitedFormat = "A helyi szolgáltatás folyamata {0} kóddal kilépett.",
        BackendHealthProbeFailedFormat = "A helyi szolgáltatás {0} egymást követő állapotellenőrzésen nem ment át.",
        BackendStartFailedFormat = "A helyi szolgáltatás indítása sikertelen: {0}",
        BackendLifecycleMonitorFailedFormat = "A helyi szolgáltatás életciklusának figyelése sikertelen: {0}",
        BackendHttpErrorFormat = "A helyi szolgáltatás HTTP {0} választ adott.",
        BackendEntryMissing = "A végleges lemezképből hiányzik a helyi háttérszolgáltatás belépési pontja.",
        BackendNotOwned = "A helyi háttérszolgáltatás nem áll készen; a natív felületnek nincs jogosultsága elindítani. Indítsa el vagy javítsa a ResourceManager.Service szolgáltatást.",
        BackendPipelineNotReady = "A helyi szolgáltatás kéréscsatornája nem áll készen.",
        NoProcessToTerminate = "Nincs leállítandó folyamat.",
        TerminateRequestCompleted = "A folyamat leállítási kérése befejeződött.",

        WebViewInitializationFailed = "A beágyazott felület inicializálása sikertelen. A részletekért nyissa meg a diagnosztikai mappát.",
        FrontendIdentityVerificationFailed = "Az előtérbeli erőforrások azonosságának ellenőrzése sikertelen. A részletekért nyissa meg a diagnosztikai mappát.",
        FrontendLoadFailed = "A helyi felületet nem sikerült betölteni az ellenőrzött szolgáltatásból.",
        WebViewRuntimeMissing = "Nem található kompatibilis WebView2 futtatókörnyezet. Telepítse a megosztott futtatókörnyezetet, és próbálja újra.",

        TrayBackendStatusConnecting = "Helyi szolgáltatás: csatlakozás",
        TrayStatusFormat = "Állapot: {0}",
        TrayOpen = "Megnyitás",
        TrayReconnectBackend = "Újracsatlakozás a helyi szolgáltatáshoz",
        TrayExit = "Kilépés",
        BackendUnavailableBalloonTitle = "A helyi szolgáltatás nem érhető el",
        NoVerifiedSession = "Még nem jött létre ellenőrizhető munkamenet a helyi szolgáltatással.",
        DiagnosticsOpenFailed = "A diagnosztikai mappát nem sikerült megnyitni.",

        SelectSoftwareRootFolder = "Válassza ki az alkalmazás gyökérmappáját",

        ForceTerminateHotkeyBalloonTitle = "Kényszerített leállítás gyorsbillentyűje",
        NoForegroundProcessToTerminate = "Nincs leállítandó előtérbeli vagy nem válaszoló alkalmazás.",
        BackendUnavailableNoTerminate = "A helyi szolgáltatás nem érhető el, ezért egyetlen folyamat sem lett leállítva.",
        ForceTerminateFailed = "A kényszerített leállítás sikertelen. Próbálja újra később.",
        ForceTerminateHotkeyDisabled = "A kényszerített leállítás gyorsbillentyűje ki van kapcsolva: tartalmaznia kell a Ctrl, Alt vagy Win billentyűt és egy nem módosító billentyűt is.",
        ForceTerminateHotkeyEnableFailed = "A kényszerített leállítás gyorsbillentyűjét nem sikerült bekapcsolni. Ellenőrizze a billentyűkombinációt, és próbálja újra.",
        HotkeyNeedsAtLeastOneKey = "Egy gyorsbillentyűhöz legalább egy billentyű szükséges.",
        ForceTerminateHotkeyNeedsModifier = "A kényszerített leállítás gyorsbillentyűjének tartalmaznia kell a Ctrl, Alt vagy Win billentyűt és egy nem módosító billentyűt is.",
        KeyboardHookRegisterFailed = "Az egyéni globális billentyűzethorgot nem sikerült regisztrálni.",
        MouseHookRegisterFailed = "Az egyéni globális egérhorgot nem sikerült regisztrálni.",

        TaskManagerHotkeyReplacement = "A Feladatkezelő gyorsbillentyűjének lecserélése",
        TaskManagerHotkeyEnableFailed = "A Feladatkezelő gyorsbillentyűjét nem sikerült bekapcsolni. Próbálja újra később.",
        TaskManagerHotkeyDisableFailed = "A Feladatkezelő gyorsbillentyűjét nem sikerült kikapcsolni. Próbálja újra később.",
        TaskManagerHotkeyForeignSetting = "Egy másik Feladatkezelő-gyorsbillentyűbeállítás található, és változatlan maradt.",
        TaskManagerHotkeyNeedsAdminToEnable = "Rendszergazdai jogosultság szükséges a Feladatkezelő gyorsbillentyűjének átvételéhez, amikor az Erőforrás-kezelő nem fut.",
        TaskManagerHotkeyNeedsAdminToDisable = "Rendszergazdai jogosultság szükséges a Feladatkezelő gyorsbillentyűje lecserélésének teljes kikapcsolásához.",
        TaskManagerHotkeyEnabled = "A Feladatkezelő gyorsbillentyűje be van kapcsolva.",
        TaskManagerHotkeyDisabled = "A Feladatkezelő gyorsbillentyűje ki van kapcsolva.",
        TaskManagerShortcutHookRegisterFailed = "A Ctrl+Shift+Esc gyorsbillentyű horgát nem sikerült regisztrálni.",
        TaskManagerShortcutHookRemoveFailed = "A Ctrl+Shift+Esc gyorsbillentyű horgát nem sikerült eltávolítani."
    };
}
