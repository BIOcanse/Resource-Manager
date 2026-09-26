namespace ResourceManager.NativeUi.Localization.Texts;

internal static class FiFi
{
    public static readonly NativeText Text = new()
    {
        AppName = "Resurssienhallinta",
        WindowTitleFormat = "Resurssienhallinta - {0}",
        TrayTooltipFormat = "Resurssienhallinta - {0}",

        AvailabilityPanelName = "Paikallisen palvelun tila",
        ConnectingTitle = "Yhdistetään paikalliseen palveluun",
        ReconnectingTitle = "Yhdistetään uudelleen paikalliseen palveluun",
        ConnectingDetail = "Tarkistetaan palvelun identiteetti ja muodostetaan istunto.",
        RetryButton = "Yritä uudelleen",
        RetryButtonDescription = "Yhdistä paikalliseen palveluun heti uudelleen",
        DiagnosticsButton = "Avaa diagnostiikkakansio",
        DiagnosticsButtonDescription = "Avaa paikallisten diagnostiikkatiedostojen kansio",
        ExitButton = "Lopeta",
        ExitButtonDescription = "Lopeta Resurssienhallinta",
        BackendUnavailableTitle = "Paikallinen palvelu ei ole tilapäisesti käytettävissä",
        BackendUnavailableDetail = "Resurssienhallinta yrittää yhdistää edelleen taustalla.",
        FrontendLoadingTitle = "Ladataan käyttöliittymää",
        FrontendLoadingDetail = "Paikallinen palvelu on vahvistettu. Sovelluksen käyttöliittymää ladataan.",
        FrontendUnavailableTitle = "Sovelluksen käyttöliittymä ei ole tilapäisesti käytettävissä",
        FrontendUnavailableDetail = "Paikallinen palvelu on yhä käynnissä, joten käyttöliittymän voi ladata uudelleen.",

        StatusConnecting = "Yhdistetään paikalliseen palveluun",
        StatusReconnecting = "Yhdistetään uudelleen paikalliseen palveluun",
        StatusBackendUnavailable = "Paikallinen palvelu ei ole käytettävissä",
        StatusShuttingDown = "Lopetetaan",
        StatusBackendReady = "Paikallinen palvelu on valmis",
        StatusFrontendLoading = "Ladataan käyttöliittymää",
        StatusReady = "Valmis",
        StatusFrontendUnavailable = "Käyttöliittymä ei ole käytettävissä",
        ActionReconnectBackend = "Yhdistä uudelleen paikalliseen palveluun",
        ActionReloadFrontend = "Lataa käyttöliittymä uudelleen",

        BackendProcessExitedFormat = "Paikallisen palvelun prosessi päättyi koodilla {0}.",
        BackendHealthProbeFailedFormat = "Paikallinen palvelu epäonnistui {0} peräkkäisessä kuntotarkistuksessa.",
        BackendStartFailedFormat = "Paikallisen palvelun käynnistäminen epäonnistui: {0}",
        BackendLifecycleMonitorFailedFormat = "Paikallisen palvelun elinkaaren valvonta epäonnistui: {0}",
        BackendHttpErrorFormat = "Paikallinen palvelu palautti HTTP {0}.",
        BackendEntryMissing = "Lopullisesta vedoksesta puuttuu paikallisen taustapalvelun aloituskohta.",
        BackendNotOwned = "Paikallinen taustapalvelu ei ole valmis; natiivilla käyttöliittymällä ei ole oikeutta käynnistää palvelua. Käynnistä tai korjaa ResourceManager.Service.",
        BackendPipelineNotReady = "Paikallisen palvelun pyyntöputki ei ole valmis.",
        NoProcessToTerminate = "Lopetettavaa prosessia ei ole.",
        TerminateRequestCompleted = "Prosessin lopetuspyyntö on valmis.",

        WebViewInitializationFailed = "Upotetun käyttöliittymän alustaminen epäonnistui. Katso lisätiedot diagnostiikkakansiosta.",
        FrontendIdentityVerificationFailed = "Käyttöliittymäresurssien identiteetin tarkistus epäonnistui. Katso lisätiedot diagnostiikkakansiosta.",
        FrontendLoadFailed = "Paikallista käyttöliittymää ei voitu ladata vahvistetusta palvelusta.",
        WebViewRuntimeMissing = "Yhteensopivaa WebView2-suoritusympäristöä ei löytynyt. Asenna jaettu suoritusympäristö ja yritä uudelleen.",

        TrayBackendStatusConnecting = "Paikallinen palvelu: yhdistetään",
        TrayStatusFormat = "Tila: {0}",
        TrayOpen = "Avaa",
        TrayReconnectBackend = "Yhdistä uudelleen paikalliseen palveluun",
        TrayExit = "Lopeta",
        BackendUnavailableBalloonTitle = "Paikallinen palvelu ei ole käytettävissä",
        NoVerifiedSession = "Vahvistettavaa istuntoa paikallisen palvelun kanssa ei ole vielä muodostettu.",
        DiagnosticsOpenFailed = "Diagnostiikkakansiota ei voitu avata.",

        SelectSoftwareRootFolder = "Valitse sovelluksen juurikansio",

        ForceTerminateHotkeyBalloonTitle = "Pakotetun lopetuksen pikanäppäin",
        NoForegroundProcessToTerminate = "Lopetettavaa edustalla olevaa tai vastaamatonta sovellusta ei ole.",
        BackendUnavailableNoTerminate = "Paikallinen palvelu ei ole käytettävissä, joten yhtään prosessia ei lopetettu.",
        ForceTerminateFailed = "Pakotettu lopetus epäonnistui. Yritä myöhemmin uudelleen.",
        ForceTerminateHotkeyDisabled = "Pakotetun lopetuksen pikanäppäin on poistettu käytöstä: siinä on oltava Ctrl, Alt tai Win yhdessä muun kuin muokkausnäppäimen kanssa.",
        ForceTerminateHotkeyEnableFailed = "Pakotetun lopetuksen pikanäppäintä ei voitu ottaa käyttöön. Tarkista näppäinyhdistelmä ja yritä uudelleen.",
        HotkeyNeedsAtLeastOneKey = "Pikanäppäin vaatii vähintään yhden näppäimen.",
        ForceTerminateHotkeyNeedsModifier = "Pakotetun lopetuksen pikanäppäimessä on oltava Ctrl, Alt tai Win yhdessä muun kuin muokkausnäppäimen kanssa.",
        KeyboardHookRegisterFailed = "Mukautettua yleistä näppäimistökoukkua ei voitu rekisteröidä.",
        MouseHookRegisterFailed = "Mukautettua yleistä hiirikoukkua ei voitu rekisteröidä.",

        TaskManagerHotkeyReplacement = "Tehtävienhallinnan pikanäppäimen korvaaminen",
        TaskManagerHotkeyEnableFailed = "Tehtävienhallinnan pikanäppäintä ei voitu ottaa käyttöön. Yritä myöhemmin uudelleen.",
        TaskManagerHotkeyDisableFailed = "Tehtävienhallinnan pikanäppäintä ei voitu poistaa käytöstä. Yritä myöhemmin uudelleen.",
        TaskManagerHotkeyForeignSetting = "Havaittiin toinen Tehtävienhallinnan pikanäppäinasetus, ja se jätettiin ennalleen.",
        TaskManagerHotkeyNeedsAdminToEnable = "Tehtävienhallinnan pikanäppäimen haltuunotto Resurssienhallinnan ollessa suljettuna vaatii järjestelmänvalvojan oikeudet.",
        TaskManagerHotkeyNeedsAdminToDisable = "Tehtävienhallinnan pikanäppäimen korvauksen täydellinen poistaminen käytöstä vaatii järjestelmänvalvojan oikeudet.",
        TaskManagerHotkeyEnabled = "Tehtävienhallinnan pikanäppäin on käytössä.",
        TaskManagerHotkeyDisabled = "Tehtävienhallinnan pikanäppäin on poistettu käytöstä.",
        TaskManagerShortcutHookRegisterFailed = "Ctrl+Vaihto+Esc-pikanäppäimen koukkua ei voitu rekisteröidä.",
        TaskManagerShortcutHookRemoveFailed = "Ctrl+Vaihto+Esc-pikanäppäimen koukkua ei voitu poistaa."
    };
}
