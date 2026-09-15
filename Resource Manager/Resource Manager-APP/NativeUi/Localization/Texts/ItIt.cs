namespace ResourceManager.NativeUi.Localization.Texts;

internal static class ItIt
{
    public static readonly NativeText Text = new()
    {
        AppName = "Gestione risorse",
        WindowTitleFormat = "Gestione risorse - {0}",
        TrayTooltipFormat = "Gestione risorse - {0}",

        AvailabilityPanelName = "Stato del servizio locale",
        ConnectingTitle = "Connessione al servizio locale",
        ReconnectingTitle = "Riconnessione al servizio locale",
        ConnectingDetail = "Verifica dell'identità del servizio e creazione della sessione.",
        RetryButton = "Riprova",
        RetryButtonDescription = "Riconnettiti subito al servizio locale",
        DiagnosticsButton = "Apri la cartella di diagnostica",
        DiagnosticsButtonDescription = "Apri la cartella dei file di diagnostica locali",
        ExitButton = "Esci",
        ExitButtonDescription = "Esci da Gestione risorse",
        BackendUnavailableTitle = "Il servizio locale non è temporaneamente disponibile",
        BackendUnavailableDetail = "Gestione risorse continua a tentare la connessione in background.",
        FrontendLoadingTitle = "Caricamento dell'interfaccia",
        FrontendLoadingDetail = "Il servizio locale è verificato; caricamento dell'interfaccia dell'applicazione.",
        FrontendUnavailableTitle = "L'interfaccia dell'applicazione non è temporaneamente disponibile",
        FrontendUnavailableDetail = "Il servizio locale è ancora in esecuzione, quindi l'interfaccia può essere ricaricata.",

        StatusConnecting = "Connessione al servizio locale",
        StatusReconnecting = "Riconnessione al servizio locale",
        StatusBackendUnavailable = "Servizio locale non disponibile",
        StatusShuttingDown = "Chiusura in corso",
        StatusBackendReady = "Servizio locale pronto",
        StatusFrontendLoading = "Caricamento dell'interfaccia",
        StatusReady = "Pronto",
        StatusFrontendUnavailable = "Interfaccia non disponibile",
        ActionReconnectBackend = "Riconnettiti al servizio locale",
        ActionReloadFrontend = "Ricarica l'interfaccia",

        BackendProcessExitedFormat = "Il processo del servizio locale è terminato con il codice {0}.",
        BackendHealthProbeFailedFormat = "Il servizio locale ha fallito {0} controlli di integrità consecutivi.",
        BackendStartFailedFormat = "Impossibile avviare il servizio locale: {0}",
        BackendLifecycleMonitorFailedFormat = "Monitoraggio del ciclo di vita del servizio locale non riuscito: {0}",
        BackendHttpErrorFormat = "Il servizio locale ha restituito HTTP {0}.",
        BackendEntryMissing = "Nell'immagine finale manca il punto di ingresso del servizio backend locale.",
        BackendNotOwned = "Il servizio backend locale non è pronto; l'interfaccia nativa non dispone dell'autorizzazione per avviarlo. Avvia o ripara ResourceManager.Service.",
        BackendPipelineNotReady = "La pipeline delle richieste del servizio locale non è pronta.",
        NoProcessToTerminate = "Non ci sono processi da terminare.",
        TerminateRequestCompleted = "La richiesta di terminazione del processo è stata completata.",

        WebViewInitializationFailed = "Inizializzazione dell'interfaccia incorporata non riuscita. Apri la cartella di diagnostica per i dettagli.",
        FrontendIdentityVerificationFailed = "Verifica dell'identità delle risorse front-end non riuscita. Apri la cartella di diagnostica per i dettagli.",
        FrontendLoadFailed = "Impossibile caricare l'interfaccia locale dal servizio verificato.",
        WebViewRuntimeMissing = "Nessun runtime WebView2 compatibile trovato. Installa il runtime condiviso e riprova.",

        TrayBackendStatusConnecting = "Servizio locale: connessione",
        TrayStatusFormat = "Stato: {0}",
        TrayOpen = "Apri",
        TrayReconnectBackend = "Riconnettiti al servizio locale",
        TrayExit = "Esci",
        BackendUnavailableBalloonTitle = "Servizio locale non disponibile",
        NoVerifiedSession = "Non è ancora stata stabilita una sessione verificabile con il servizio locale.",
        DiagnosticsOpenFailed = "Impossibile aprire la cartella di diagnostica.",

        SelectSoftwareRootFolder = "Seleziona la cartella radice dell'applicazione",

        ForceTerminateHotkeyBalloonTitle = "Tasto di scelta rapida per la terminazione forzata",
        NoForegroundProcessToTerminate = "Non c'è nessuna applicazione in primo piano o che non risponde da terminare.",
        BackendUnavailableNoTerminate = "Il servizio locale non è disponibile, quindi nessun processo è stato terminato.",
        ForceTerminateFailed = "Terminazione forzata non riuscita. Riprova più tardi.",
        ForceTerminateHotkeyDisabled = "Il tasto di scelta rapida per la terminazione forzata è disattivato: deve combinare Ctrl, Alt o Win con un tasto non modificatore.",
        ForceTerminateHotkeyEnableFailed = "Impossibile attivare il tasto di scelta rapida per la terminazione forzata. Controlla la combinazione di tasti e riprova.",
        HotkeyNeedsAtLeastOneKey = "Un tasto di scelta rapida richiede almeno un tasto.",
        ForceTerminateHotkeyNeedsModifier = "Il tasto di scelta rapida per la terminazione forzata deve combinare Ctrl, Alt o Win con un tasto non modificatore.",
        KeyboardHookRegisterFailed = "Impossibile registrare l'hook globale personalizzato della tastiera.",
        MouseHookRegisterFailed = "Impossibile registrare l'hook globale personalizzato del mouse.",

        TaskManagerHotkeyReplacement = "Sostituzione della scelta rapida di Gestione attività",
        TaskManagerHotkeyEnableFailed = "Impossibile attivare la scelta rapida di Gestione attività. Riprova più tardi.",
        TaskManagerHotkeyDisableFailed = "Impossibile disattivare la scelta rapida di Gestione attività. Riprova più tardi.",
        TaskManagerHotkeyForeignSetting = "È stata rilevata un'altra impostazione della scelta rapida di Gestione attività ed è stata lasciata invariata.",
        TaskManagerHotkeyNeedsAdminToEnable = "Sono necessari i diritti di amministratore per assumere il controllo della scelta rapida di Gestione attività quando Gestione risorse non è in esecuzione.",
        TaskManagerHotkeyNeedsAdminToDisable = "Sono necessari i diritti di amministratore per disattivare completamente la sostituzione della scelta rapida di Gestione attività.",
        TaskManagerHotkeyEnabled = "La scelta rapida di Gestione attività è attiva.",
        TaskManagerHotkeyDisabled = "La scelta rapida di Gestione attività è disattivata.",
        TaskManagerShortcutHookRegisterFailed = "Impossibile registrare l'hook della scelta rapida Ctrl+Maiusc+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Impossibile rimuovere l'hook della scelta rapida Ctrl+Maiusc+Esc."
    };
}
