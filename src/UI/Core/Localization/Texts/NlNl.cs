namespace ResourceManager.NativeUi.Localization.Texts;

internal static class NlNl
{
    public static readonly NativeText Text = new()
    {
        AppName = "Resourcebeheer",
        WindowTitleFormat = "Resourcebeheer - {0}",
        TrayTooltipFormat = "Resourcebeheer - {0}",

        AvailabilityPanelName = "Status van de lokale service",
        ConnectingTitle = "Verbinding maken met de lokale service",
        ReconnectingTitle = "Opnieuw verbinding maken met de lokale service",
        ConnectingDetail = "De identiteit van de service wordt geverifieerd en er wordt een sessie opgezet.",
        RetryButton = "Opnieuw proberen",
        RetryButtonDescription = "Nu opnieuw verbinding maken met de lokale service",
        DiagnosticsButton = "Diagnosemap openen",
        DiagnosticsButtonDescription = "De map met lokale diagnosebestanden openen",
        ExitButton = "Afsluiten",
        ExitButtonDescription = "Resourcebeheer afsluiten",
        BackendUnavailableTitle = "De lokale service is tijdelijk niet beschikbaar",
        BackendUnavailableDetail = "Resourcebeheer blijft op de achtergrond proberen verbinding te maken.",
        FrontendLoadingTitle = "De interface wordt geladen",
        FrontendLoadingDetail = "De lokale service is geverifieerd; de interface van de app wordt geladen.",
        FrontendUnavailableTitle = "De interface van de app is tijdelijk niet beschikbaar",
        FrontendUnavailableDetail = "De lokale service draait nog, dus de interface kan opnieuw worden geladen.",

        StatusConnecting = "Verbinding maken met de lokale service",
        StatusReconnecting = "Opnieuw verbinding maken met de lokale service",
        StatusBackendUnavailable = "Lokale service niet beschikbaar",
        StatusShuttingDown = "Afsluiten",
        StatusBackendReady = "Lokale service gereed",
        StatusFrontendLoading = "De interface wordt geladen",
        StatusReady = "Gereed",
        StatusFrontendUnavailable = "Interface niet beschikbaar",
        ActionReconnectBackend = "Opnieuw verbinding maken met de lokale service",
        ActionReloadFrontend = "Interface opnieuw laden",

        BackendProcessExitedFormat = "Het proces van de lokale service is afgesloten met code {0}.",
        BackendHealthProbeFailedFormat = "De lokale service heeft {0} opeenvolgende statuscontroles niet doorstaan.",
        BackendStartFailedFormat = "De lokale service kan niet worden gestart: {0}",
        BackendLifecycleMonitorFailedFormat = "Het bewaken van de levenscyclus van de lokale service is mislukt: {0}",
        BackendHttpErrorFormat = "De lokale service heeft HTTP {0} geretourneerd.",
        BackendEntryMissing = "Het toegangspunt van de lokale backendservice ontbreekt in de uiteindelijke image.",
        BackendNotOwned = "De lokale backendservice is niet gereed; de native interface heeft geen recht om de service te starten. Start of herstel ResourceManager.Service.",
        BackendPipelineNotReady = "De aanvraagpijplijn van de lokale service is niet gereed.",
        NoProcessToTerminate = "Er is geen proces om te beëindigen.",
        TerminateRequestCompleted = "De aanvraag om het proces te beëindigen is voltooid.",

        WebViewInitializationFailed = "De ingesloten interface kan niet worden geïnitialiseerd. Open de diagnosemap voor details.",
        FrontendIdentityVerificationFailed = "De identiteitsverificatie van de frontendbestanden is mislukt. Open de diagnosemap voor details.",
        FrontendLoadFailed = "De lokale interface kan niet vanuit de geverifieerde service worden geladen.",
        WebViewRuntimeMissing = "Er is geen compatibele WebView2-runtime gevonden. Installeer de gedeelde runtime en probeer het opnieuw.",

        TrayBackendStatusConnecting = "Lokale service: verbinden",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Openen",
        TrayReconnectBackend = "Opnieuw verbinding maken met de lokale service",
        TrayExit = "Afsluiten",
        BackendUnavailableBalloonTitle = "Lokale service niet beschikbaar",
        NoVerifiedSession = "Er is nog geen verifieerbare sessie met de lokale service tot stand gebracht.",
        DiagnosticsOpenFailed = "De diagnosemap kan niet worden geopend.",

        SelectSoftwareRootFolder = "Selecteer de hoofdmap van de toepassing",

        ForceTerminateHotkeyBalloonTitle = "Sneltoets voor geforceerd beëindigen",
        NoForegroundProcessToTerminate = "Er is geen toepassing op de voorgrond of zonder reactie om te beëindigen.",
        BackendUnavailableNoTerminate = "De lokale service is niet beschikbaar, dus er is geen proces beëindigd.",
        ForceTerminateFailed = "Geforceerd beëindigen is mislukt. Probeer het later opnieuw.",
        ForceTerminateHotkeyDisabled = "De sneltoets voor geforceerd beëindigen is uitgeschakeld: hij moet Ctrl, Alt of Win combineren met een niet-modificatietoets.",
        ForceTerminateHotkeyEnableFailed = "De sneltoets voor geforceerd beëindigen kan niet worden ingeschakeld. Controleer de toetsencombinatie en probeer het opnieuw.",
        HotkeyNeedsAtLeastOneKey = "Een sneltoets heeft ten minste één toets nodig.",
        ForceTerminateHotkeyNeedsModifier = "De sneltoets voor geforceerd beëindigen moet Ctrl, Alt of Win combineren met een niet-modificatietoets.",
        KeyboardHookRegisterFailed = "De aangepaste globale toetsenbordhook kan niet worden geregistreerd.",
        MouseHookRegisterFailed = "De aangepaste globale muishook kan niet worden geregistreerd.",

        TaskManagerHotkeyReplacement = "Vervanging van de sneltoets van Taakbeheer",
        TaskManagerHotkeyEnableFailed = "De sneltoets van Taakbeheer kan niet worden ingeschakeld. Probeer het later opnieuw.",
        TaskManagerHotkeyDisableFailed = "De sneltoets van Taakbeheer kan niet worden uitgeschakeld. Probeer het later opnieuw.",
        TaskManagerHotkeyForeignSetting = "Er is een andere instelling voor de sneltoets van Taakbeheer gevonden; deze is ongewijzigd gelaten.",
        TaskManagerHotkeyNeedsAdminToEnable = "Beheerdersrechten zijn vereist om de sneltoets van Taakbeheer over te nemen wanneer Resourcebeheer niet actief is.",
        TaskManagerHotkeyNeedsAdminToDisable = "Beheerdersrechten zijn vereist om de vervanging van de sneltoets van Taakbeheer volledig uit te schakelen.",
        TaskManagerHotkeyEnabled = "De sneltoets van Taakbeheer is ingeschakeld.",
        TaskManagerHotkeyDisabled = "De sneltoets van Taakbeheer is uitgeschakeld.",
        TaskManagerShortcutHookRegisterFailed = "De hook voor de sneltoets Ctrl+Shift+Esc kan niet worden geregistreerd.",
        TaskManagerShortcutHookRemoveFailed = "De hook voor de sneltoets Ctrl+Shift+Esc kan niet worden verwijderd."
    };
}
