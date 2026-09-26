namespace ResourceManager.NativeUi.Localization.Texts;

internal static class DaDk
{
    public static readonly NativeText Text = new()
    {
        AppName = "Ressourcestyring",
        WindowTitleFormat = "Ressourcestyring - {0}",
        TrayTooltipFormat = "Ressourcestyring - {0}",

        AvailabilityPanelName = "Status for den lokale tjeneste",
        ConnectingTitle = "Opretter forbindelse til den lokale tjeneste",
        ReconnectingTitle = "Genopretter forbindelse til den lokale tjeneste",
        ConnectingDetail = "Tjenestens identitet bekræftes, og sessionen oprettes.",
        RetryButton = "Prøv igen",
        RetryButtonDescription = "Opret forbindelse til den lokale tjeneste igen nu",
        DiagnosticsButton = "Åbn diagnosticeringsmappen",
        DiagnosticsButtonDescription = "Åbn mappen med lokale diagnosticeringsfiler",
        ExitButton = "Afslut",
        ExitButtonDescription = "Afslut Ressourcestyring",
        BackendUnavailableTitle = "Den lokale tjeneste er midlertidigt utilgængelig",
        BackendUnavailableDetail = "Ressourcestyring forsøger fortsat at oprette forbindelse i baggrunden.",
        FrontendLoadingTitle = "Indlæser brugerfladen",
        FrontendLoadingDetail = "Den lokale tjeneste er bekræftet. Appens brugerflade indlæses.",
        FrontendUnavailableTitle = "Appens brugerflade er midlertidigt utilgængelig",
        FrontendUnavailableDetail = "Den lokale tjeneste kører stadig, så brugerfladen kan indlæses igen.",

        StatusConnecting = "Opretter forbindelse til den lokale tjeneste",
        StatusReconnecting = "Genopretter forbindelse til den lokale tjeneste",
        StatusBackendUnavailable = "Den lokale tjeneste er utilgængelig",
        StatusShuttingDown = "Afslutter",
        StatusBackendReady = "Den lokale tjeneste er klar",
        StatusFrontendLoading = "Indlæser brugerfladen",
        StatusReady = "Klar",
        StatusFrontendUnavailable = "Brugerfladen er utilgængelig",
        ActionReconnectBackend = "Opret forbindelse til den lokale tjeneste igen",
        ActionReloadFrontend = "Indlæs brugerfladen igen",

        BackendProcessExitedFormat = "Processen for den lokale tjeneste blev afsluttet med koden {0}.",
        BackendHealthProbeFailedFormat = "Den lokale tjeneste fejlede {0} helbredskontroller i træk.",
        BackendStartFailedFormat = "Den lokale tjeneste kunne ikke startes: {0}",
        BackendLifecycleMonitorFailedFormat = "Overvågningen af den lokale tjenestes livscyklus mislykkedes: {0}",
        BackendHttpErrorFormat = "Den lokale tjeneste returnerede HTTP {0}.",
        BackendEntryMissing = "Indgangspunktet for den lokale backend-tjeneste mangler i det endelige image.",
        BackendNotOwned = "Den lokale backend-tjeneste er ikke klar; den oprindelige brugerflade har ikke tilladelse til at starte tjenesten. Start eller reparer ResourceManager.Service.",
        BackendPipelineNotReady = "Anmodningspipelinen for den lokale tjeneste er ikke klar.",
        NoProcessToTerminate = "Der er ingen proces at afslutte.",
        TerminateRequestCompleted = "Anmodningen om at afslutte processen er fuldført.",

        WebViewInitializationFailed = "Den integrerede brugerflade kunne ikke initialiseres. Åbn diagnosticeringsmappen for at se detaljer.",
        FrontendIdentityVerificationFailed = "Identitetsbekræftelsen af frontend-ressourcerne mislykkedes. Åbn diagnosticeringsmappen for at se detaljer.",
        FrontendLoadFailed = "Den lokale brugerflade kunne ikke indlæses fra den bekræftede tjeneste.",
        WebViewRuntimeMissing = "Der blev ikke fundet en kompatibel WebView2-runtime. Installer den delte runtime, og prøv igen.",

        TrayBackendStatusConnecting = "Lokal tjeneste: opretter forbindelse",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Åbn",
        TrayReconnectBackend = "Opret forbindelse til den lokale tjeneste igen",
        TrayExit = "Afslut",
        BackendUnavailableBalloonTitle = "Den lokale tjeneste er utilgængelig",
        NoVerifiedSession = "Der er endnu ikke oprettet en session med den lokale tjeneste, som kan bekræftes.",
        DiagnosticsOpenFailed = "Diagnosticeringsmappen kunne ikke åbnes.",

        SelectSoftwareRootFolder = "Vælg appens rodmappe",

        ForceTerminateHotkeyBalloonTitle = "Genvejstast til tvungen afslutning",
        NoForegroundProcessToTerminate = "Der er ingen app i forgrunden eller app uden svar at afslutte.",
        BackendUnavailableNoTerminate = "Den lokale tjeneste er utilgængelig, så ingen proces blev afsluttet.",
        ForceTerminateFailed = "Den tvungne afslutning mislykkedes. Prøv igen senere.",
        ForceTerminateHotkeyDisabled = "Genvejstasten til tvungen afslutning er deaktiveret: Den skal kombinere Ctrl, Alt eller Win med en tast, der ikke er en modifikator.",
        ForceTerminateHotkeyEnableFailed = "Genvejstasten til tvungen afslutning kunne ikke aktiveres. Kontrollér tastekombinationen, og prøv igen.",
        HotkeyNeedsAtLeastOneKey = "En genvejstast kræver mindst én tast.",
        ForceTerminateHotkeyNeedsModifier = "Genvejstasten til tvungen afslutning skal kombinere Ctrl, Alt eller Win med en tast, der ikke er en modifikator.",
        KeyboardHookRegisterFailed = "Den brugerdefinerede globale tastaturhook kunne ikke registreres.",
        MouseHookRegisterFailed = "Den brugerdefinerede globale musehook kunne ikke registreres.",

        TaskManagerHotkeyReplacement = "Erstatning af Jobliste-genvejstasten",
        TaskManagerHotkeyEnableFailed = "Jobliste-genvejstasten kunne ikke aktiveres. Prøv igen senere.",
        TaskManagerHotkeyDisableFailed = "Jobliste-genvejstasten kunne ikke deaktiveres. Prøv igen senere.",
        TaskManagerHotkeyForeignSetting = "En anden indstilling for Jobliste-genvejstasten blev fundet og er bevaret uændret.",
        TaskManagerHotkeyNeedsAdminToEnable = "Der kræves administratorrettigheder for at overtage Jobliste-genvejstasten, når Ressourcestyring ikke kører.",
        TaskManagerHotkeyNeedsAdminToDisable = "Der kræves administratorrettigheder for helt at slå erstatningen af Jobliste-genvejstasten fra.",
        TaskManagerHotkeyEnabled = "Jobliste-genvejstasten er aktiveret.",
        TaskManagerHotkeyDisabled = "Jobliste-genvejstasten er deaktiveret.",
        TaskManagerShortcutHookRegisterFailed = "Hooken til genvejen Ctrl+Skift+Esc kunne ikke registreres.",
        TaskManagerShortcutHookRemoveFailed = "Hooken til genvejen Ctrl+Skift+Esc kunne ikke fjernes."
    };
}
