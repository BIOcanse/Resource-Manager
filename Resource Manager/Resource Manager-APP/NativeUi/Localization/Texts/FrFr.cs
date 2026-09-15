namespace ResourceManager.NativeUi.Localization.Texts;

internal static class FrFr
{
    public static readonly NativeText Text = new()
    {
        AppName = "Gestionnaire de ressources",
        WindowTitleFormat = "Gestionnaire de ressources - {0}",
        TrayTooltipFormat = "Gestionnaire de ressources - {0}",

        AvailabilityPanelName = "État du service local",
        ConnectingTitle = "Connexion au service local",
        ReconnectingTitle = "Reconnexion au service local",
        ConnectingDetail = "Vérification de l'identité du service et établissement de la session.",
        RetryButton = "Réessayer",
        RetryButtonDescription = "Se reconnecter immédiatement au service local",
        DiagnosticsButton = "Ouvrir le dossier de diagnostics",
        DiagnosticsButtonDescription = "Ouvrir le dossier des fichiers de diagnostic locaux",
        ExitButton = "Quitter",
        ExitButtonDescription = "Quitter le Gestionnaire de ressources",
        BackendUnavailableTitle = "Le service local est temporairement indisponible",
        BackendUnavailableDetail = "Le Gestionnaire de ressources continue d'essayer de se connecter en arrière-plan.",
        FrontendLoadingTitle = "Chargement de l'interface",
        FrontendLoadingDetail = "Le service local est vérifié ; chargement de l'interface de l'application.",
        FrontendUnavailableTitle = "L'interface de l'application est temporairement indisponible",
        FrontendUnavailableDetail = "Le service local fonctionne toujours, l'interface peut être rechargée.",

        StatusConnecting = "Connexion au service local",
        StatusReconnecting = "Reconnexion au service local",
        StatusBackendUnavailable = "Service local indisponible",
        StatusShuttingDown = "Fermeture en cours",
        StatusBackendReady = "Service local prêt",
        StatusFrontendLoading = "Chargement de l'interface",
        StatusReady = "Prêt",
        StatusFrontendUnavailable = "Interface indisponible",
        ActionReconnectBackend = "Se reconnecter au service local",
        ActionReloadFrontend = "Recharger l'interface",

        BackendProcessExitedFormat = "Le processus du service local s'est arrêté avec le code {0}.",
        BackendHealthProbeFailedFormat = "Le service local a échoué à {0} vérifications d'état consécutives.",
        BackendStartFailedFormat = "Échec du démarrage du service local : {0}",
        BackendLifecycleMonitorFailedFormat = "Échec de la surveillance du cycle de vie du service local : {0}",
        BackendHttpErrorFormat = "Le service local a renvoyé HTTP {0}.",
        BackendEntryMissing = "Le point d'entrée du service backend local est absent de l'image finale.",
        BackendNotOwned = "Le service backend local n'est pas prêt ; l'interface native ne détient pas le droit de démarrer le service. Démarrez ou réparez ResourceManager.Service.",
        BackendPipelineNotReady = "Le pipeline de requêtes du service local n'est pas prêt.",
        NoProcessToTerminate = "Aucun processus à arrêter.",
        TerminateRequestCompleted = "La demande d'arrêt du processus est terminée.",

        WebViewInitializationFailed = "Échec de l'initialisation de l'interface intégrée. Ouvrez le dossier de diagnostics pour plus de détails.",
        FrontendIdentityVerificationFailed = "Échec de la vérification d'identité des ressources front-end. Ouvrez le dossier de diagnostics pour plus de détails.",
        FrontendLoadFailed = "L'interface locale n'a pas pu être chargée depuis le service vérifié.",
        WebViewRuntimeMissing = "Aucun runtime WebView2 compatible n'a été trouvé. Installez le runtime partagé et réessayez.",

        TrayBackendStatusConnecting = "Service local : connexion",
        TrayStatusFormat = "État : {0}",
        TrayOpen = "Ouvrir",
        TrayReconnectBackend = "Se reconnecter au service local",
        TrayExit = "Quitter",
        BackendUnavailableBalloonTitle = "Service local indisponible",
        NoVerifiedSession = "Aucune session vérifiable du service local n'a encore été établie.",
        DiagnosticsOpenFailed = "Impossible d'ouvrir le dossier de diagnostics.",

        SelectSoftwareRootFolder = "Sélectionner le dossier racine de l'application",

        ForceTerminateHotkeyBalloonTitle = "Raccourci d'arrêt forcé",
        NoForegroundProcessToTerminate = "Aucune application au premier plan ou ne répondant pas à arrêter.",
        BackendUnavailableNoTerminate = "Le service local est indisponible : aucun processus n'a été arrêté.",
        ForceTerminateFailed = "L'arrêt forcé a échoué. Réessayez plus tard.",
        ForceTerminateHotkeyDisabled = "Le raccourci d'arrêt forcé est désactivé : il doit combiner Ctrl, Alt ou Win avec une touche non modificatrice.",
        ForceTerminateHotkeyEnableFailed = "Impossible d'activer le raccourci d'arrêt forcé. Vérifiez la combinaison de touches et réessayez.",
        HotkeyNeedsAtLeastOneKey = "Un raccourci nécessite au moins une touche.",
        ForceTerminateHotkeyNeedsModifier = "Le raccourci d'arrêt forcé doit combiner Ctrl, Alt ou Win avec une touche non modificatrice.",
        KeyboardHookRegisterFailed = "Impossible d'enregistrer le hook clavier global personnalisé.",
        MouseHookRegisterFailed = "Impossible d'enregistrer le hook souris global personnalisé.",

        TaskManagerHotkeyReplacement = "Remplacement du raccourci du Gestionnaire des tâches",
        TaskManagerHotkeyEnableFailed = "Impossible d'activer le raccourci du Gestionnaire des tâches. Réessayez plus tard.",
        TaskManagerHotkeyDisableFailed = "Impossible de désactiver le raccourci du Gestionnaire des tâches. Réessayez plus tard.",
        TaskManagerHotkeyForeignSetting = "Un autre réglage de raccourci du Gestionnaire des tâches a été détecté et conservé tel quel.",
        TaskManagerHotkeyNeedsAdminToEnable = "Des droits d'administrateur sont requis pour reprendre le raccourci du Gestionnaire des tâches lorsque le Gestionnaire de ressources n'est pas en cours d'exécution.",
        TaskManagerHotkeyNeedsAdminToDisable = "Des droits d'administrateur sont requis pour désactiver complètement le remplacement du raccourci du Gestionnaire des tâches.",
        TaskManagerHotkeyEnabled = "Le raccourci du Gestionnaire des tâches est activé.",
        TaskManagerHotkeyDisabled = "Le raccourci du Gestionnaire des tâches est désactivé.",
        TaskManagerShortcutHookRegisterFailed = "Impossible d'enregistrer le hook du raccourci Ctrl+Maj+Échap.",
        TaskManagerShortcutHookRemoveFailed = "Impossible de supprimer le hook du raccourci Ctrl+Maj+Échap."
    };
}
