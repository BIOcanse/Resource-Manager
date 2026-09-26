namespace ResourceManager.NativeUi.Localization.Texts;

internal static class SvSe
{
    public static readonly NativeText Text = new()
    {
        AppName = "Resurshanteraren",
        WindowTitleFormat = "Resurshanteraren - {0}",
        TrayTooltipFormat = "Resurshanteraren - {0}",

        AvailabilityPanelName = "Status för den lokala tjänsten",
        ConnectingTitle = "Ansluter till den lokala tjänsten",
        ReconnectingTitle = "Återansluter till den lokala tjänsten",
        ConnectingDetail = "Verifierar tjänstens identitet och upprättar sessionen.",
        RetryButton = "Försök igen",
        RetryButtonDescription = "Återanslut till den lokala tjänsten nu",
        DiagnosticsButton = "Öppna diagnostikmappen",
        DiagnosticsButtonDescription = "Öppna mappen med lokala diagnostikfiler",
        ExitButton = "Avsluta",
        ExitButtonDescription = "Avsluta Resurshanteraren",
        BackendUnavailableTitle = "Den lokala tjänsten är tillfälligt otillgänglig",
        BackendUnavailableDetail = "Resurshanteraren fortsätter att försöka ansluta i bakgrunden.",
        FrontendLoadingTitle = "Läser in gränssnittet",
        FrontendLoadingDetail = "Den lokala tjänsten är verifierad. Programmets gränssnitt läses in.",
        FrontendUnavailableTitle = "Programmets gränssnitt är tillfälligt otillgängligt",
        FrontendUnavailableDetail = "Den lokala tjänsten körs fortfarande, så gränssnittet kan läsas in igen.",

        StatusConnecting = "Ansluter till den lokala tjänsten",
        StatusReconnecting = "Återansluter till den lokala tjänsten",
        StatusBackendUnavailable = "Den lokala tjänsten är otillgänglig",
        StatusShuttingDown = "Avslutar",
        StatusBackendReady = "Den lokala tjänsten är klar",
        StatusFrontendLoading = "Läser in gränssnittet",
        StatusReady = "Klar",
        StatusFrontendUnavailable = "Gränssnittet är otillgängligt",
        ActionReconnectBackend = "Återanslut till den lokala tjänsten",
        ActionReloadFrontend = "Läs in gränssnittet igen",

        BackendProcessExitedFormat = "Den lokala tjänstens process avslutades med kod {0}.",
        BackendHealthProbeFailedFormat = "Den lokala tjänsten misslyckades med {0} hälsokontroller i rad.",
        BackendStartFailedFormat = "Det gick inte att starta den lokala tjänsten: {0}",
        BackendLifecycleMonitorFailedFormat = "Övervakningen av den lokala tjänstens livscykel misslyckades: {0}",
        BackendHttpErrorFormat = "Den lokala tjänsten returnerade HTTP {0}.",
        BackendEntryMissing = "Startpunkten för den lokala serverdelstjänsten saknas i den slutliga avbildningen.",
        BackendNotOwned = "Den lokala serverdelstjänsten är inte klar. Det inbyggda gränssnittet har inte behörighet att starta tjänsten. Starta eller reparera ResourceManager.Service.",
        BackendPipelineNotReady = "Den lokala tjänstens begärandepipeline är inte klar.",
        NoProcessToTerminate = "Det finns ingen process att avsluta.",
        TerminateRequestCompleted = "Begäran om att avsluta processen har slutförts.",

        WebViewInitializationFailed = "Det inbäddade gränssnittet kunde inte initieras. Öppna diagnostikmappen för mer information.",
        FrontendIdentityVerificationFailed = "Identitetsverifieringen av klientresurserna misslyckades. Öppna diagnostikmappen för mer information.",
        FrontendLoadFailed = "Det lokala gränssnittet kunde inte läsas in från den verifierade tjänsten.",
        WebViewRuntimeMissing = "Ingen kompatibel WebView2-körning hittades. Installera den delade körningen och försök igen.",

        TrayBackendStatusConnecting = "Lokal tjänst: ansluter",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Öppna",
        TrayReconnectBackend = "Återanslut till den lokala tjänsten",
        TrayExit = "Avsluta",
        BackendUnavailableBalloonTitle = "Den lokala tjänsten är otillgänglig",
        NoVerifiedSession = "Ingen verifierbar session med den lokala tjänsten har upprättats än.",
        DiagnosticsOpenFailed = "Diagnostikmappen kunde inte öppnas.",

        SelectSoftwareRootFolder = "Välj programmets rotmapp",

        ForceTerminateHotkeyBalloonTitle = "Kortkommando för tvingad avslutning",
        NoForegroundProcessToTerminate = "Det finns inget förgrundsprogram eller program som inte svarar att avsluta.",
        BackendUnavailableNoTerminate = "Den lokala tjänsten är otillgänglig, så ingen process avslutades.",
        ForceTerminateFailed = "Den tvingade avslutningen misslyckades. Försök igen senare.",
        ForceTerminateHotkeyDisabled = "Kortkommandot för tvingad avslutning är inaktiverat: det måste kombinera Ctrl, Alt eller Win med en tangent som inte är en modifierare.",
        ForceTerminateHotkeyEnableFailed = "Kortkommandot för tvingad avslutning kunde inte aktiveras. Kontrollera tangentkombinationen och försök igen.",
        HotkeyNeedsAtLeastOneKey = "Ett kortkommando kräver minst en tangent.",
        ForceTerminateHotkeyNeedsModifier = "Kortkommandot för tvingad avslutning måste kombinera Ctrl, Alt eller Win med en tangent som inte är en modifierare.",
        KeyboardHookRegisterFailed = "Den anpassade globala tangentbordskroken kunde inte registreras.",
        MouseHookRegisterFailed = "Den anpassade globala muskroken kunde inte registreras.",

        TaskManagerHotkeyReplacement = "Ersättning av Aktivitetshanterarens kortkommando",
        TaskManagerHotkeyEnableFailed = "Aktivitetshanterarens kortkommando kunde inte aktiveras. Försök igen senare.",
        TaskManagerHotkeyDisableFailed = "Aktivitetshanterarens kortkommando kunde inte inaktiveras. Försök igen senare.",
        TaskManagerHotkeyForeignSetting = "En annan inställning för Aktivitetshanterarens kortkommando upptäcktes och lämnades oförändrad.",
        TaskManagerHotkeyNeedsAdminToEnable = "Administratörsbehörighet krävs för att ta över Aktivitetshanterarens kortkommando när Resurshanteraren inte körs.",
        TaskManagerHotkeyNeedsAdminToDisable = "Administratörsbehörighet krävs för att helt stänga av ersättningen av Aktivitetshanterarens kortkommando.",
        TaskManagerHotkeyEnabled = "Aktivitetshanterarens kortkommando är aktiverat.",
        TaskManagerHotkeyDisabled = "Aktivitetshanterarens kortkommando är inaktiverat.",
        TaskManagerShortcutHookRegisterFailed = "Kroken för kortkommandot Ctrl+Skift+Esc kunde inte registreras.",
        TaskManagerShortcutHookRemoveFailed = "Kroken för kortkommandot Ctrl+Skift+Esc kunde inte tas bort."
    };
}
