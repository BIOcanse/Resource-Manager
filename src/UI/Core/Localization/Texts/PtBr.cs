namespace ResourceManager.NativeUi.Localization.Texts;

internal static class PtBr
{
    public static readonly NativeText Text = new()
    {
        AppName = "Gerenciador de Recursos",
        WindowTitleFormat = "Gerenciador de Recursos - {0}",
        TrayTooltipFormat = "Gerenciador de Recursos - {0}",

        AvailabilityPanelName = "Status do serviço local",
        ConnectingTitle = "Conectando ao serviço local",
        ReconnectingTitle = "Reconectando ao serviço local",
        ConnectingDetail = "Verificando a identidade do serviço e estabelecendo a sessão.",
        RetryButton = "Tentar novamente",
        RetryButtonDescription = "Reconectar ao serviço local agora",
        DiagnosticsButton = "Abrir a pasta de diagnósticos",
        DiagnosticsButtonDescription = "Abrir a pasta de arquivos de diagnóstico locais",
        ExitButton = "Sair",
        ExitButtonDescription = "Sair do Gerenciador de Recursos",
        BackendUnavailableTitle = "O serviço local está temporariamente indisponível",
        BackendUnavailableDetail = "O Gerenciador de Recursos continua tentando se conectar em segundo plano.",
        FrontendLoadingTitle = "Carregando a interface",
        FrontendLoadingDetail = "O serviço local foi verificado; carregando a interface do aplicativo.",
        FrontendUnavailableTitle = "A interface do aplicativo está temporariamente indisponível",
        FrontendUnavailableDetail = "O serviço local continua em execução, então a interface pode ser recarregada.",

        StatusConnecting = "Conectando ao serviço local",
        StatusReconnecting = "Reconectando ao serviço local",
        StatusBackendUnavailable = "Serviço local indisponível",
        StatusShuttingDown = "Saindo",
        StatusBackendReady = "Serviço local pronto",
        StatusFrontendLoading = "Carregando a interface",
        StatusReady = "Pronto",
        StatusFrontendUnavailable = "Interface indisponível",
        ActionReconnectBackend = "Reconectar ao serviço local",
        ActionReloadFrontend = "Recarregar a interface",

        BackendProcessExitedFormat = "O processo do serviço local foi encerrado com o código {0}.",
        BackendHealthProbeFailedFormat = "O serviço local falhou em {0} verificações de integridade consecutivas.",
        BackendStartFailedFormat = "Falha ao iniciar o serviço local: {0}",
        BackendLifecycleMonitorFailedFormat = "Falha no monitoramento do ciclo de vida do serviço local: {0}",
        BackendHttpErrorFormat = "O serviço local retornou HTTP {0}.",
        BackendEntryMissing = "O ponto de entrada do serviço de back-end local está ausente na imagem final.",
        BackendNotOwned = "O serviço de back-end local não está pronto; a interface nativa não tem permissão para iniciar o serviço. Inicie ou repare o ResourceManager.Service.",
        BackendPipelineNotReady = "O pipeline de solicitações do serviço local não está pronto.",
        NoProcessToTerminate = "Não há nenhum processo para encerrar.",
        TerminateRequestCompleted = "A solicitação de encerramento do processo foi concluída.",

        WebViewInitializationFailed = "Falha ao inicializar a interface incorporada. Abra a pasta de diagnósticos para ver os detalhes.",
        FrontendIdentityVerificationFailed = "Falha na verificação de identidade dos recursos de front-end. Abra a pasta de diagnósticos para ver os detalhes.",
        FrontendLoadFailed = "Não foi possível carregar a interface local a partir do serviço verificado.",
        WebViewRuntimeMissing = "Nenhum runtime do WebView2 compatível foi encontrado. Instale o runtime compartilhado e tente novamente.",

        TrayBackendStatusConnecting = "Serviço local: conectando",
        TrayStatusFormat = "Status: {0}",
        TrayOpen = "Abrir",
        TrayReconnectBackend = "Reconectar ao serviço local",
        TrayExit = "Sair",
        BackendUnavailableBalloonTitle = "Serviço local indisponível",
        NoVerifiedSession = "Ainda não foi estabelecida uma sessão verificável com o serviço local.",
        DiagnosticsOpenFailed = "Não foi possível abrir a pasta de diagnósticos.",

        SelectSoftwareRootFolder = "Selecionar a pasta raiz do aplicativo",

        ForceTerminateHotkeyBalloonTitle = "Atalho de encerramento forçado",
        NoForegroundProcessToTerminate = "Não há nenhum aplicativo em primeiro plano ou sem resposta para encerrar.",
        BackendUnavailableNoTerminate = "O serviço local está indisponível, então nenhum processo foi encerrado.",
        ForceTerminateFailed = "O encerramento forçado falhou. Tente novamente mais tarde.",
        ForceTerminateHotkeyDisabled = "O atalho de encerramento forçado está desativado: ele precisa combinar Ctrl, Alt ou Win com uma tecla não modificadora.",
        ForceTerminateHotkeyEnableFailed = "Não foi possível ativar o atalho de encerramento forçado. Verifique a combinação de teclas e tente novamente.",
        HotkeyNeedsAtLeastOneKey = "Um atalho precisa de pelo menos uma tecla.",
        ForceTerminateHotkeyNeedsModifier = "O atalho de encerramento forçado precisa combinar Ctrl, Alt ou Win com uma tecla não modificadora.",
        KeyboardHookRegisterFailed = "Não foi possível registrar o hook global de teclado personalizado.",
        MouseHookRegisterFailed = "Não foi possível registrar o hook global de mouse personalizado.",

        TaskManagerHotkeyReplacement = "Substituição do atalho do Gerenciador de Tarefas",
        TaskManagerHotkeyEnableFailed = "Não foi possível ativar o atalho do Gerenciador de Tarefas. Tente novamente mais tarde.",
        TaskManagerHotkeyDisableFailed = "Não foi possível desativar o atalho do Gerenciador de Tarefas. Tente novamente mais tarde.",
        TaskManagerHotkeyForeignSetting = "Outra configuração de atalho do Gerenciador de Tarefas foi detectada e mantida sem alterações.",
        TaskManagerHotkeyNeedsAdminToEnable = "É necessária permissão de administrador para assumir o atalho do Gerenciador de Tarefas enquanto o Gerenciador de Recursos não está em execução.",
        TaskManagerHotkeyNeedsAdminToDisable = "É necessária permissão de administrador para desativar completamente a substituição do atalho do Gerenciador de Tarefas.",
        TaskManagerHotkeyEnabled = "O atalho do Gerenciador de Tarefas está ativado.",
        TaskManagerHotkeyDisabled = "O atalho do Gerenciador de Tarefas está desativado.",
        TaskManagerShortcutHookRegisterFailed = "Não foi possível registrar o hook do atalho Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Não foi possível remover o hook do atalho Ctrl+Shift+Esc."
    };
}
