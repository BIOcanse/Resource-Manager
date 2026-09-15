namespace ResourceManager.NativeUi.Localization.Texts;

internal static class RoRo
{
    public static readonly NativeText Text = new()
    {
        AppName = "Manager de resurse",
        WindowTitleFormat = "Manager de resurse - {0}",
        TrayTooltipFormat = "Manager de resurse - {0}",

        AvailabilityPanelName = "Starea serviciului local",
        ConnectingTitle = "Se conectează la serviciul local",
        ReconnectingTitle = "Se reconectează la serviciul local",
        ConnectingDetail = "Se verifică identitatea serviciului și se stabilește sesiunea.",
        RetryButton = "Reîncearcă",
        RetryButtonDescription = "Reconectează-te acum la serviciul local",
        DiagnosticsButton = "Deschide folderul de diagnosticare",
        DiagnosticsButtonDescription = "Deschide folderul cu fișierele locale de diagnosticare",
        ExitButton = "Ieșire",
        ExitButtonDescription = "Ieși din Managerul de resurse",
        BackendUnavailableTitle = "Serviciul local este temporar indisponibil",
        BackendUnavailableDetail = "Managerul de resurse continuă să încerce conectarea în fundal.",
        FrontendLoadingTitle = "Se încarcă interfața",
        FrontendLoadingDetail = "Serviciul local este verificat; se încarcă interfața aplicației.",
        FrontendUnavailableTitle = "Interfața aplicației este temporar indisponibilă",
        FrontendUnavailableDetail = "Serviciul local rulează în continuare, așa că interfața poate fi reîncărcată.",

        StatusConnecting = "Se conectează la serviciul local",
        StatusReconnecting = "Se reconectează la serviciul local",
        StatusBackendUnavailable = "Serviciul local este indisponibil",
        StatusShuttingDown = "Se închide",
        StatusBackendReady = "Serviciul local este pregătit",
        StatusFrontendLoading = "Se încarcă interfața",
        StatusReady = "Pregătit",
        StatusFrontendUnavailable = "Interfața este indisponibilă",
        ActionReconnectBackend = "Reconectează-te la serviciul local",
        ActionReloadFrontend = "Reîncarcă interfața",

        BackendProcessExitedFormat = "Procesul serviciului local s-a încheiat cu codul {0}.",
        BackendHealthProbeFailedFormat = "Serviciul local a eșuat la {0} verificări de stare consecutive.",
        BackendStartFailedFormat = "Serviciul local nu a putut porni: {0}",
        BackendLifecycleMonitorFailedFormat = "Monitorizarea ciclului de viață al serviciului local a eșuat: {0}",
        BackendHttpErrorFormat = "Serviciul local a returnat HTTP {0}.",
        BackendEntryMissing = "Punctul de intrare al serviciului backend local lipsește din imaginea finală.",
        BackendNotOwned = "Serviciul backend local nu este pregătit; interfața nativă nu are dreptul de a porni serviciul. Porniți sau reparați ResourceManager.Service.",
        BackendPipelineNotReady = "Conducta de cereri a serviciului local nu este pregătită.",
        NoProcessToTerminate = "Nu există niciun proces de încheiat.",
        TerminateRequestCompleted = "Cererea de încheiere a procesului s-a finalizat.",

        WebViewInitializationFailed = "Interfața încorporată nu a putut fi inițializată. Deschideți folderul de diagnosticare pentru detalii.",
        FrontendIdentityVerificationFailed = "Verificarea identității resurselor de interfață a eșuat. Deschideți folderul de diagnosticare pentru detalii.",
        FrontendLoadFailed = "Interfața locală nu a putut fi încărcată din serviciul verificat.",
        WebViewRuntimeMissing = "Nu a fost găsit niciun runtime WebView2 compatibil. Instalați runtime-ul partajat și încercați din nou.",

        TrayBackendStatusConnecting = "Serviciu local: se conectează",
        TrayStatusFormat = "Stare: {0}",
        TrayOpen = "Deschide",
        TrayReconnectBackend = "Reconectează-te la serviciul local",
        TrayExit = "Ieșire",
        BackendUnavailableBalloonTitle = "Serviciul local este indisponibil",
        NoVerifiedSession = "Încă nu a fost stabilită o sesiune verificabilă cu serviciul local.",
        DiagnosticsOpenFailed = "Folderul de diagnosticare nu a putut fi deschis.",

        SelectSoftwareRootFolder = "Selectați folderul rădăcină al aplicației",

        ForceTerminateHotkeyBalloonTitle = "Comanda rapidă pentru încheiere forțată",
        NoForegroundProcessToTerminate = "Nu există nicio aplicație în prim-plan sau care nu răspunde de încheiat.",
        BackendUnavailableNoTerminate = "Serviciul local este indisponibil, așa că niciun proces nu a fost încheiat.",
        ForceTerminateFailed = "Încheierea forțată a eșuat. Încercați din nou mai târziu.",
        ForceTerminateHotkeyDisabled = "Comanda rapidă pentru încheiere forțată este dezactivată: trebuie să combine Ctrl, Alt sau Win cu o tastă care nu este modificator.",
        ForceTerminateHotkeyEnableFailed = "Comanda rapidă pentru încheiere forțată nu a putut fi activată. Verificați combinația de taste și încercați din nou.",
        HotkeyNeedsAtLeastOneKey = "O comandă rapidă necesită cel puțin o tastă.",
        ForceTerminateHotkeyNeedsModifier = "Comanda rapidă pentru încheiere forțată trebuie să combine Ctrl, Alt sau Win cu o tastă care nu este modificator.",
        KeyboardHookRegisterFailed = "Cârligul global personalizat de tastatură nu a putut fi înregistrat.",
        MouseHookRegisterFailed = "Cârligul global personalizat de mouse nu a putut fi înregistrat.",

        TaskManagerHotkeyReplacement = "Înlocuirea comenzii rapide a Managerului de activități",
        TaskManagerHotkeyEnableFailed = "Comanda rapidă a Managerului de activități nu a putut fi activată. Încercați din nou mai târziu.",
        TaskManagerHotkeyDisableFailed = "Comanda rapidă a Managerului de activități nu a putut fi dezactivată. Încercați din nou mai târziu.",
        TaskManagerHotkeyForeignSetting = "A fost detectată o altă setare pentru comanda rapidă a Managerului de activități și a fost păstrată neschimbată.",
        TaskManagerHotkeyNeedsAdminToEnable = "Sunt necesare drepturi de administrator pentru a prelua comanda rapidă a Managerului de activități când Managerul de resurse nu rulează.",
        TaskManagerHotkeyNeedsAdminToDisable = "Sunt necesare drepturi de administrator pentru a dezactiva complet înlocuirea comenzii rapide a Managerului de activități.",
        TaskManagerHotkeyEnabled = "Comanda rapidă a Managerului de activități este activată.",
        TaskManagerHotkeyDisabled = "Comanda rapidă a Managerului de activități este dezactivată.",
        TaskManagerShortcutHookRegisterFailed = "Cârligul pentru comanda rapidă Ctrl+Shift+Esc nu a putut fi înregistrat.",
        TaskManagerShortcutHookRemoveFailed = "Cârligul pentru comanda rapidă Ctrl+Shift+Esc nu a putut fi eliminat."
    };
}
