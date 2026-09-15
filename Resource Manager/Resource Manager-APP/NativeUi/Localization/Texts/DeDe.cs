namespace ResourceManager.NativeUi.Localization.Texts;

internal static class DeDe
{
    public static readonly NativeText Text = new()
    {
        AppName = "Ressourcen-Manager",
        WindowTitleFormat = "Ressourcen-Manager - {0}",
        TrayTooltipFormat = "Ressourcen-Manager - {0}",

        AvailabilityPanelName = "Status des lokalen Diensts",
        ConnectingTitle = "Verbindung mit dem lokalen Dienst wird hergestellt",
        ReconnectingTitle = "Verbindung mit dem lokalen Dienst wird wiederhergestellt",
        ConnectingDetail = "Die Dienstidentität wird überprüft und eine Sitzung aufgebaut.",
        RetryButton = "Wiederholen",
        RetryButtonDescription = "Sofort erneut mit dem lokalen Dienst verbinden",
        DiagnosticsButton = "Diagnoseordner öffnen",
        DiagnosticsButtonDescription = "Ordner mit den lokalen Diagnosedateien öffnen",
        ExitButton = "Beenden",
        ExitButtonDescription = "Ressourcen-Manager beenden",
        BackendUnavailableTitle = "Der lokale Dienst ist vorübergehend nicht verfügbar",
        BackendUnavailableDetail = "Der Ressourcen-Manager versucht im Hintergrund weiterhin, eine Verbindung herzustellen.",
        FrontendLoadingTitle = "Oberfläche wird geladen",
        FrontendLoadingDetail = "Der lokale Dienst ist verifiziert; die Anwendungsoberfläche wird geladen.",
        FrontendUnavailableTitle = "Die Anwendungsoberfläche ist vorübergehend nicht verfügbar",
        FrontendUnavailableDetail = "Der lokale Dienst läuft weiterhin, die Oberfläche kann neu geladen werden.",

        StatusConnecting = "Verbindung mit dem lokalen Dienst wird hergestellt",
        StatusReconnecting = "Verbindung mit dem lokalen Dienst wird wiederhergestellt",
        StatusBackendUnavailable = "Lokaler Dienst nicht verfügbar",
        StatusShuttingDown = "Wird beendet",
        StatusBackendReady = "Lokaler Dienst bereit",
        StatusFrontendLoading = "Oberfläche wird geladen",
        StatusReady = "Bereit",
        StatusFrontendUnavailable = "Oberfläche nicht verfügbar",
        ActionReconnectBackend = "Erneut mit dem lokalen Dienst verbinden",
        ActionReloadFrontend = "Oberfläche neu laden",

        BackendProcessExitedFormat = "Der Prozess des lokalen Diensts wurde mit Code {0} beendet.",
        BackendHealthProbeFailedFormat = "Beim lokalen Dienst sind {0} Integritätsprüfungen in Folge fehlgeschlagen.",
        BackendStartFailedFormat = "Der lokale Dienst konnte nicht gestartet werden: {0}",
        BackendLifecycleMonitorFailedFormat = "Die Lebenszyklusüberwachung des lokalen Diensts ist fehlgeschlagen: {0}",
        BackendHttpErrorFormat = "Der lokale Dienst hat HTTP {0} zurückgegeben.",
        BackendEntryMissing = "Im finalen Image fehlt der Einstiegspunkt des lokalen Backend-Diensts.",
        BackendNotOwned = "Der lokale Backend-Dienst ist nicht bereit; die native Oberfläche besitzt keine Berechtigung zum Starten des Diensts. Starten oder reparieren Sie ResourceManager.Service.",
        BackendPipelineNotReady = "Die Anforderungspipeline des lokalen Diensts ist nicht bereit.",
        NoProcessToTerminate = "Es gibt keinen Prozess zum Beenden.",
        TerminateRequestCompleted = "Die Anforderung zum Beenden des Prozesses wurde abgeschlossen.",

        WebViewInitializationFailed = "Die eingebettete Oberfläche konnte nicht initialisiert werden. Öffnen Sie den Diagnoseordner für Details.",
        FrontendIdentityVerificationFailed = "Die Identitätsprüfung der Frontend-Ressourcen ist fehlgeschlagen. Öffnen Sie den Diagnoseordner für Details.",
        FrontendLoadFailed = "Die lokale Oberfläche konnte nicht vom verifizierten Dienst geladen werden.",
        WebViewRuntimeMissing = "Es wurde keine kompatible WebView2-Runtime gefunden. Installieren Sie die gemeinsame Runtime und versuchen Sie es erneut.",

        TrayBackendStatusConnecting = "Lokaler Dienst: Verbindung wird hergestellt",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Öffnen",
        TrayReconnectBackend = "Erneut mit dem lokalen Dienst verbinden",
        TrayExit = "Beenden",
        BackendUnavailableBalloonTitle = "Lokaler Dienst nicht verfügbar",
        NoVerifiedSession = "Es wurde noch keine überprüfbare Sitzung mit dem lokalen Dienst aufgebaut.",
        DiagnosticsOpenFailed = "Der Diagnoseordner konnte nicht geöffnet werden.",

        SelectSoftwareRootFolder = "Stammordner der Anwendung auswählen",

        ForceTerminateHotkeyBalloonTitle = "Tastenkombination zum erzwungenen Beenden",
        NoForegroundProcessToTerminate = "Es gibt keine Vordergrund- oder nicht reagierende Anwendung zum Beenden.",
        BackendUnavailableNoTerminate = "Der lokale Dienst ist nicht verfügbar, daher wurde kein Prozess beendet.",
        ForceTerminateFailed = "Das erzwungene Beenden ist fehlgeschlagen. Versuchen Sie es später erneut.",
        ForceTerminateHotkeyDisabled = "Die Tastenkombination zum erzwungenen Beenden ist deaktiviert: Sie muss Strg, Alt oder Win zusammen mit einer Nicht-Modifikatortaste enthalten.",
        ForceTerminateHotkeyEnableFailed = "Die Tastenkombination zum erzwungenen Beenden konnte nicht aktiviert werden. Prüfen Sie die Tastenbelegung und versuchen Sie es erneut.",
        HotkeyNeedsAtLeastOneKey = "Eine Tastenkombination benötigt mindestens eine Taste.",
        ForceTerminateHotkeyNeedsModifier = "Die Tastenkombination zum erzwungenen Beenden muss Strg, Alt oder Win zusammen mit einer Nicht-Modifikatortaste enthalten.",
        KeyboardHookRegisterFailed = "Der benutzerdefinierte globale Tastatur-Hook konnte nicht registriert werden.",
        MouseHookRegisterFailed = "Der benutzerdefinierte globale Maus-Hook konnte nicht registriert werden.",

        TaskManagerHotkeyReplacement = "Ersetzen der Task-Manager-Tastenkombination",
        TaskManagerHotkeyEnableFailed = "Die Task-Manager-Tastenkombination konnte nicht aktiviert werden. Versuchen Sie es später erneut.",
        TaskManagerHotkeyDisableFailed = "Die Task-Manager-Tastenkombination konnte nicht deaktiviert werden. Versuchen Sie es später erneut.",
        TaskManagerHotkeyForeignSetting = "Eine andere Task-Manager-Tastenkombination wurde erkannt und unverändert beibehalten.",
        TaskManagerHotkeyNeedsAdminToEnable = "Administratorrechte sind erforderlich, um die Task-Manager-Tastenkombination zu übernehmen, während der Ressourcen-Manager nicht läuft.",
        TaskManagerHotkeyNeedsAdminToDisable = "Administratorrechte sind erforderlich, um das Ersetzen der Task-Manager-Tastenkombination vollständig zu deaktivieren.",
        TaskManagerHotkeyEnabled = "Die Task-Manager-Tastenkombination ist aktiviert.",
        TaskManagerHotkeyDisabled = "Die Task-Manager-Tastenkombination ist deaktiviert.",
        TaskManagerShortcutHookRegisterFailed = "Der Hook für die Tastenkombination Strg+Umschalt+Esc konnte nicht registriert werden.",
        TaskManagerShortcutHookRemoveFailed = "Der Hook für die Tastenkombination Strg+Umschalt+Esc konnte nicht entfernt werden."
    };
}
