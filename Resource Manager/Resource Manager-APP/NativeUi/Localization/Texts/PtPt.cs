namespace ResourceManager.NativeUi.Localization.Texts;

internal static class PtPt
{
    public static readonly NativeText Text = new()
    {
        AppName = "Gestor de Recursos",
        WindowTitleFormat = "Gestor de Recursos - {0}",
        TrayTooltipFormat = "Gestor de Recursos - {0}",

        AvailabilityPanelName = "Estado do serviço local",
        ConnectingTitle = "A ligar ao serviço local",
        ReconnectingTitle = "A voltar a ligar ao serviço local",
        ConnectingDetail = "A verificar a identidade do serviço e a estabelecer a sessão.",
        RetryButton = "Tentar novamente",
        RetryButtonDescription = "Voltar a ligar ao serviço local agora",
        DiagnosticsButton = "Abrir a pasta de diagnósticos",
        DiagnosticsButtonDescription = "Abrir a pasta dos ficheiros de diagnóstico locais",
        ExitButton = "Sair",
        ExitButtonDescription = "Sair do Gestor de Recursos",
        BackendUnavailableTitle = "O serviço local está temporariamente indisponível",
        BackendUnavailableDetail = "O Gestor de Recursos continua a tentar ligar-se em segundo plano.",
        FrontendLoadingTitle = "A carregar a interface",
        FrontendLoadingDetail = "O serviço local foi verificado; a carregar a interface da aplicação.",
        FrontendUnavailableTitle = "A interface da aplicação está temporariamente indisponível",
        FrontendUnavailableDetail = "O serviço local continua em execução, pelo que a interface pode ser recarregada.",

        StatusConnecting = "A ligar ao serviço local",
        StatusReconnecting = "A voltar a ligar ao serviço local",
        StatusBackendUnavailable = "Serviço local indisponível",
        StatusShuttingDown = "A sair",
        StatusBackendReady = "Serviço local pronto",
        StatusFrontendLoading = "A carregar a interface",
        StatusReady = "Pronto",
        StatusFrontendUnavailable = "Interface indisponível",
        ActionReconnectBackend = "Voltar a ligar ao serviço local",
        ActionReloadFrontend = "Recarregar a interface",

        BackendProcessExitedFormat = "O processo do serviço local terminou com o código {0}.",
        BackendHealthProbeFailedFormat = "O serviço local falhou {0} verificações de estado consecutivas.",
        BackendStartFailedFormat = "Falha ao iniciar o serviço local: {0}",
        BackendLifecycleMonitorFailedFormat = "Falha na monitorização do ciclo de vida do serviço local: {0}",
        BackendHttpErrorFormat = "O serviço local devolveu HTTP {0}.",
        BackendEntryMissing = "Falta o ponto de entrada do serviço de back-end local na imagem final.",
        BackendNotOwned = "O serviço de back-end local não está pronto; a interface nativa não tem permissão para iniciar o serviço. Inicie ou repare o ResourceManager.Service.",
        BackendPipelineNotReady = "O pipeline de pedidos do serviço local não está pronto.",
        NoProcessToTerminate = "Não existe nenhum processo para terminar.",
        TerminateRequestCompleted = "O pedido de fim do processo foi concluído.",

        WebViewInitializationFailed = "Falha ao inicializar a interface incorporada. Abra a pasta de diagnósticos para ver os detalhes.",
        FrontendIdentityVerificationFailed = "Falha na verificação de identidade dos recursos de front-end. Abra a pasta de diagnósticos para ver os detalhes.",
        FrontendLoadFailed = "Não foi possível carregar a interface local a partir do serviço verificado.",
        WebViewRuntimeMissing = "Não foi encontrado um runtime do WebView2 compatível. Instale o runtime partilhado e tente novamente.",

        TrayBackendStatusConnecting = "Serviço local: a ligar",
        TrayStatusFormat = "Estado: {0}",
        TrayOpen = "Abrir",
        TrayReconnectBackend = "Voltar a ligar ao serviço local",
        TrayExit = "Sair",
        BackendUnavailableBalloonTitle = "Serviço local indisponível",
        NoVerifiedSession = "Ainda não foi estabelecida uma sessão verificável com o serviço local.",
        DiagnosticsOpenFailed = "Não foi possível abrir a pasta de diagnósticos.",

        SelectSoftwareRootFolder = "Selecionar a pasta raiz da aplicação",

        ForceTerminateHotkeyBalloonTitle = "Atalho para terminar à força",
        NoForegroundProcessToTerminate = "Não existe nenhuma aplicação em primeiro plano ou sem resposta para terminar.",
        BackendUnavailableNoTerminate = "O serviço local está indisponível, pelo que nenhum processo foi terminado.",
        ForceTerminateFailed = "Não foi possível terminar à força. Tente novamente mais tarde.",
        ForceTerminateHotkeyDisabled = "O atalho para terminar à força está desativado: tem de combinar Ctrl, Alt ou Win com uma tecla não modificadora.",
        ForceTerminateHotkeyEnableFailed = "Não foi possível ativar o atalho para terminar à força. Verifique a combinação de teclas e tente novamente.",
        HotkeyNeedsAtLeastOneKey = "Um atalho precisa de, pelo menos, uma tecla.",
        ForceTerminateHotkeyNeedsModifier = "O atalho para terminar à força tem de combinar Ctrl, Alt ou Win com uma tecla não modificadora.",
        KeyboardHookRegisterFailed = "Não foi possível registar o hook global de teclado personalizado.",
        MouseHookRegisterFailed = "Não foi possível registar o hook global de rato personalizado.",

        TaskManagerHotkeyReplacement = "Substituição do atalho do Gestor de Tarefas",
        TaskManagerHotkeyEnableFailed = "Não foi possível ativar o atalho do Gestor de Tarefas. Tente novamente mais tarde.",
        TaskManagerHotkeyDisableFailed = "Não foi possível desativar o atalho do Gestor de Tarefas. Tente novamente mais tarde.",
        TaskManagerHotkeyForeignSetting = "Foi detetada outra definição de atalho do Gestor de Tarefas e mantida sem alterações.",
        TaskManagerHotkeyNeedsAdminToEnable = "São necessárias permissões de administrador para assumir o atalho do Gestor de Tarefas enquanto o Gestor de Recursos não está em execução.",
        TaskManagerHotkeyNeedsAdminToDisable = "São necessárias permissões de administrador para desativar completamente a substituição do atalho do Gestor de Tarefas.",
        TaskManagerHotkeyEnabled = "O atalho do Gestor de Tarefas está ativado.",
        TaskManagerHotkeyDisabled = "O atalho do Gestor de Tarefas está desativado.",
        TaskManagerShortcutHookRegisterFailed = "Não foi possível registar o hook do atalho Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Não foi possível remover o hook do atalho Ctrl+Shift+Esc."
    };
}
