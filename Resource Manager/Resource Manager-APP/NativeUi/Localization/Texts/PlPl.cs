namespace ResourceManager.NativeUi.Localization.Texts;

internal static class PlPl
{
    public static readonly NativeText Text = new()
    {
        AppName = "Menedżer zasobów",
        WindowTitleFormat = "Menedżer zasobów - {0}",
        TrayTooltipFormat = "Menedżer zasobów - {0}",

        AvailabilityPanelName = "Stan usługi lokalnej",
        ConnectingTitle = "Łączenie z usługą lokalną",
        ReconnectingTitle = "Ponowne łączenie z usługą lokalną",
        ConnectingDetail = "Trwa weryfikacja tożsamości usługi i nawiązywanie sesji.",
        RetryButton = "Ponów próbę",
        RetryButtonDescription = "Połącz ponownie z usługą lokalną teraz",
        DiagnosticsButton = "Otwórz folder diagnostyki",
        DiagnosticsButtonDescription = "Otwórz folder lokalnych plików diagnostycznych",
        ExitButton = "Zakończ",
        ExitButtonDescription = "Zakończ Menedżera zasobów",
        BackendUnavailableTitle = "Usługa lokalna jest tymczasowo niedostępna",
        BackendUnavailableDetail = "Menedżer zasobów nadal próbuje połączyć się w tle.",
        FrontendLoadingTitle = "Ładowanie interfejsu",
        FrontendLoadingDetail = "Usługa lokalna została zweryfikowana; trwa ładowanie interfejsu aplikacji.",
        FrontendUnavailableTitle = "Interfejs aplikacji jest tymczasowo niedostępny",
        FrontendUnavailableDetail = "Usługa lokalna nadal działa, więc interfejs można załadować ponownie.",

        StatusConnecting = "Łączenie z usługą lokalną",
        StatusReconnecting = "Ponowne łączenie z usługą lokalną",
        StatusBackendUnavailable = "Usługa lokalna niedostępna",
        StatusShuttingDown = "Kończenie pracy",
        StatusBackendReady = "Usługa lokalna gotowa",
        StatusFrontendLoading = "Ładowanie interfejsu",
        StatusReady = "Gotowe",
        StatusFrontendUnavailable = "Interfejs niedostępny",
        ActionReconnectBackend = "Połącz ponownie z usługą lokalną",
        ActionReloadFrontend = "Załaduj interfejs ponownie",

        BackendProcessExitedFormat = "Proces usługi lokalnej zakończył się z kodem {0}.",
        BackendHealthProbeFailedFormat = "Usługa lokalna nie przeszła {0} kolejnych sprawdzeń kondycji.",
        BackendStartFailedFormat = "Nie można uruchomić usługi lokalnej: {0}",
        BackendLifecycleMonitorFailedFormat = "Monitorowanie cyklu życia usługi lokalnej nie powiodło się: {0}",
        BackendHttpErrorFormat = "Usługa lokalna zwróciła HTTP {0}.",
        BackendEntryMissing = "W obrazie końcowym brakuje punktu wejścia lokalnej usługi zaplecza.",
        BackendNotOwned = "Lokalna usługa zaplecza nie jest gotowa; natywny interfejs nie ma uprawnień do jej uruchomienia. Uruchom lub napraw ResourceManager.Service.",
        BackendPipelineNotReady = "Potok żądań usługi lokalnej nie jest gotowy.",
        NoProcessToTerminate = "Nie ma procesu do zakończenia.",
        TerminateRequestCompleted = "Żądanie zakończenia procesu zostało wykonane.",

        WebViewInitializationFailed = "Nie można zainicjować osadzonego interfejsu. Szczegóły znajdziesz w folderze diagnostyki.",
        FrontendIdentityVerificationFailed = "Weryfikacja tożsamości zasobów frontonu nie powiodła się. Szczegóły znajdziesz w folderze diagnostyki.",
        FrontendLoadFailed = "Nie można załadować lokalnego interfejsu ze zweryfikowanej usługi.",
        WebViewRuntimeMissing = "Nie znaleziono zgodnego środowiska uruchomieniowego WebView2. Zainstaluj środowisko współdzielone i spróbuj ponownie.",

        TrayBackendStatusConnecting = "Usługa lokalna: łączenie",
        TrayStatusFormat = "Stan: {0}",
        TrayOpen = "Otwórz",
        TrayReconnectBackend = "Połącz ponownie z usługą lokalną",
        TrayExit = "Zakończ",
        BackendUnavailableBalloonTitle = "Usługa lokalna niedostępna",
        NoVerifiedSession = "Nie nawiązano jeszcze weryfikowalnej sesji z usługą lokalną.",
        DiagnosticsOpenFailed = "Nie można otworzyć folderu diagnostyki.",

        SelectSoftwareRootFolder = "Wybierz folder główny aplikacji",

        ForceTerminateHotkeyBalloonTitle = "Skrót wymuszonego zakończenia",
        NoForegroundProcessToTerminate = "Nie ma aplikacji na pierwszym planie ani nieodpowiadającej aplikacji do zakończenia.",
        BackendUnavailableNoTerminate = "Usługa lokalna jest niedostępna, więc żaden proces nie został zakończony.",
        ForceTerminateFailed = "Wymuszone zakończenie nie powiodło się. Spróbuj ponownie później.",
        ForceTerminateHotkeyDisabled = "Skrót wymuszonego zakończenia jest wyłączony: musi łączyć Ctrl, Alt lub Win z klawiszem niebędącym modyfikatorem.",
        ForceTerminateHotkeyEnableFailed = "Nie można włączyć skrótu wymuszonego zakończenia. Sprawdź kombinację klawiszy i spróbuj ponownie.",
        HotkeyNeedsAtLeastOneKey = "Skrót wymaga co najmniej jednego klawisza.",
        ForceTerminateHotkeyNeedsModifier = "Skrót wymuszonego zakończenia musi łączyć Ctrl, Alt lub Win z klawiszem niebędącym modyfikatorem.",
        KeyboardHookRegisterFailed = "Nie można zarejestrować niestandardowego globalnego zaczepu klawiatury.",
        MouseHookRegisterFailed = "Nie można zarejestrować niestandardowego globalnego zaczepu myszy.",

        TaskManagerHotkeyReplacement = "Zastąpienie skrótu Menedżera zadań",
        TaskManagerHotkeyEnableFailed = "Nie można włączyć skrótu Menedżera zadań. Spróbuj ponownie później.",
        TaskManagerHotkeyDisableFailed = "Nie można wyłączyć skrótu Menedżera zadań. Spróbuj ponownie później.",
        TaskManagerHotkeyForeignSetting = "Wykryto inne ustawienie skrótu Menedżera zadań i pozostawiono je bez zmian.",
        TaskManagerHotkeyNeedsAdminToEnable = "Przejęcie skrótu Menedżera zadań, gdy Menedżer zasobów nie jest uruchomiony, wymaga uprawnień administratora.",
        TaskManagerHotkeyNeedsAdminToDisable = "Całkowite wyłączenie zastępowania skrótu Menedżera zadań wymaga uprawnień administratora.",
        TaskManagerHotkeyEnabled = "Skrót Menedżera zadań jest włączony.",
        TaskManagerHotkeyDisabled = "Skrót Menedżera zadań jest wyłączony.",
        TaskManagerShortcutHookRegisterFailed = "Nie można zarejestrować zaczepu skrótu Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Nie można usunąć zaczepu skrótu Ctrl+Shift+Esc."
    };
}
