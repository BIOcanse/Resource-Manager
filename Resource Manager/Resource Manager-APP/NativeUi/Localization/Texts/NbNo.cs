namespace ResourceManager.NativeUi.Localization.Texts;

internal static class NbNo
{
    public static readonly NativeText Text = new()
    {
        AppName = "Ressursbehandling",
        WindowTitleFormat = "Ressursbehandling - {0}",
        TrayTooltipFormat = "Ressursbehandling - {0}",

        AvailabilityPanelName = "Status for den lokale tjenesten",
        ConnectingTitle = "Kobler til den lokale tjenesten",
        ReconnectingTitle = "Kobler til den lokale tjenesten på nytt",
        ConnectingDetail = "Bekrefter tjenestens identitet og oppretter økten.",
        RetryButton = "Prøv på nytt",
        RetryButtonDescription = "Koble til den lokale tjenesten på nytt nå",
        DiagnosticsButton = "Åpne diagnostikkmappen",
        DiagnosticsButtonDescription = "Åpne mappen med lokale diagnostikkfiler",
        ExitButton = "Avslutt",
        ExitButtonDescription = "Avslutt Ressursbehandling",
        BackendUnavailableTitle = "Den lokale tjenesten er midlertidig utilgjengelig",
        BackendUnavailableDetail = "Ressursbehandling fortsetter å prøve å koble til i bakgrunnen.",
        FrontendLoadingTitle = "Laster inn grensesnittet",
        FrontendLoadingDetail = "Den lokale tjenesten er bekreftet. Appens grensesnitt lastes inn.",
        FrontendUnavailableTitle = "Appens grensesnitt er midlertidig utilgjengelig",
        FrontendUnavailableDetail = "Den lokale tjenesten kjører fortsatt, så grensesnittet kan lastes inn på nytt.",

        StatusConnecting = "Kobler til den lokale tjenesten",
        StatusReconnecting = "Kobler til den lokale tjenesten på nytt",
        StatusBackendUnavailable = "Den lokale tjenesten er utilgjengelig",
        StatusShuttingDown = "Avslutter",
        StatusBackendReady = "Den lokale tjenesten er klar",
        StatusFrontendLoading = "Laster inn grensesnittet",
        StatusReady = "Klar",
        StatusFrontendUnavailable = "Grensesnittet er utilgjengelig",
        ActionReconnectBackend = "Koble til den lokale tjenesten på nytt",
        ActionReloadFrontend = "Last inn grensesnittet på nytt",

        BackendProcessExitedFormat = "Prosessen til den lokale tjenesten ble avsluttet med koden {0}.",
        BackendHealthProbeFailedFormat = "Den lokale tjenesten mislyktes i {0} helsesjekker på rad.",
        BackendStartFailedFormat = "Kunne ikke starte den lokale tjenesten: {0}",
        BackendLifecycleMonitorFailedFormat = "Overvåkingen av livssyklusen til den lokale tjenesten mislyktes: {0}",
        BackendHttpErrorFormat = "Den lokale tjenesten returnerte HTTP {0}.",
        BackendEntryMissing = "Inngangspunktet for den lokale serversidetjenesten mangler i det endelige avbildet.",
        BackendNotOwned = "Den lokale serversidetjenesten er ikke klar; det opprinnelige grensesnittet har ikke tillatelse til å starte tjenesten. Start eller reparer ResourceManager.Service.",
        BackendPipelineNotReady = "Forespørselspipelinen til den lokale tjenesten er ikke klar.",
        NoProcessToTerminate = "Det finnes ingen prosess å avslutte.",
        TerminateRequestCompleted = "Forespørselen om å avslutte prosessen er fullført.",

        WebViewInitializationFailed = "Det innebygde grensesnittet kunne ikke initialiseres. Åpne diagnostikkmappen for detaljer.",
        FrontendIdentityVerificationFailed = "Identitetsbekreftelsen av frontend-ressursene mislyktes. Åpne diagnostikkmappen for detaljer.",
        FrontendLoadFailed = "Det lokale grensesnittet kunne ikke lastes inn fra den bekreftede tjenesten.",
        WebViewRuntimeMissing = "Fant ingen kompatibel WebView2-kjøretid. Installer den delte kjøretiden og prøv på nytt.",

        TrayBackendStatusConnecting = "Lokal tjeneste: kobler til",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Åpne",
        TrayReconnectBackend = "Koble til den lokale tjenesten på nytt",
        TrayExit = "Avslutt",
        BackendUnavailableBalloonTitle = "Den lokale tjenesten er utilgjengelig",
        NoVerifiedSession = "Det er ennå ikke opprettet en økt med den lokale tjenesten som kan bekreftes.",
        DiagnosticsOpenFailed = "Diagnostikkmappen kunne ikke åpnes.",

        SelectSoftwareRootFolder = "Velg appens rotmappe",

        ForceTerminateHotkeyBalloonTitle = "Hurtigtast for tvungen avslutning",
        NoForegroundProcessToTerminate = "Det finnes ingen app i forgrunnen eller app som ikke svarer å avslutte.",
        BackendUnavailableNoTerminate = "Den lokale tjenesten er utilgjengelig, så ingen prosess ble avsluttet.",
        ForceTerminateFailed = "Den tvungne avslutningen mislyktes. Prøv igjen senere.",
        ForceTerminateHotkeyDisabled = "Hurtigtasten for tvungen avslutning er deaktivert: den må kombinere Ctrl, Alt eller Win med en tast som ikke er en modifikator.",
        ForceTerminateHotkeyEnableFailed = "Hurtigtasten for tvungen avslutning kunne ikke aktiveres. Kontroller tastekombinasjonen og prøv på nytt.",
        HotkeyNeedsAtLeastOneKey = "En hurtigtast krever minst én tast.",
        ForceTerminateHotkeyNeedsModifier = "Hurtigtasten for tvungen avslutning må kombinere Ctrl, Alt eller Win med en tast som ikke er en modifikator.",
        KeyboardHookRegisterFailed = "Den egendefinerte globale tastaturhooken kunne ikke registreres.",
        MouseHookRegisterFailed = "Den egendefinerte globale musehooken kunne ikke registreres.",

        TaskManagerHotkeyReplacement = "Erstatning av hurtigtasten for Oppgavebehandling",
        TaskManagerHotkeyEnableFailed = "Hurtigtasten for Oppgavebehandling kunne ikke aktiveres. Prøv igjen senere.",
        TaskManagerHotkeyDisableFailed = "Hurtigtasten for Oppgavebehandling kunne ikke deaktiveres. Prøv igjen senere.",
        TaskManagerHotkeyForeignSetting = "En annen innstilling for hurtigtasten til Oppgavebehandling ble oppdaget og er beholdt uendret.",
        TaskManagerHotkeyNeedsAdminToEnable = "Det kreves administratorrettigheter for å overta hurtigtasten for Oppgavebehandling når Ressursbehandling ikke kjører.",
        TaskManagerHotkeyNeedsAdminToDisable = "Det kreves administratorrettigheter for å slå av erstatningen av hurtigtasten for Oppgavebehandling helt.",
        TaskManagerHotkeyEnabled = "Hurtigtasten for Oppgavebehandling er aktivert.",
        TaskManagerHotkeyDisabled = "Hurtigtasten for Oppgavebehandling er deaktivert.",
        TaskManagerShortcutHookRegisterFailed = "Hooken for hurtigtasten Ctrl+Shift+Esc kunne ikke registreres.",
        TaskManagerShortcutHookRemoveFailed = "Hooken for hurtigtasten Ctrl+Shift+Esc kunne ikke fjernes."
    };
}
