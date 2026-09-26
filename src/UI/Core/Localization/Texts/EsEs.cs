namespace ResourceManager.NativeUi.Localization.Texts;

internal static class EsEs
{
    public static readonly NativeText Text = new()
    {
        AppName = "Administrador de recursos",
        WindowTitleFormat = "Administrador de recursos - {0}",
        TrayTooltipFormat = "Administrador de recursos - {0}",

        AvailabilityPanelName = "Estado del servicio local",
        ConnectingTitle = "Conectando con el servicio local",
        ReconnectingTitle = "Reconectando con el servicio local",
        ConnectingDetail = "Verificando la identidad del servicio y estableciendo la sesión.",
        RetryButton = "Reintentar",
        RetryButtonDescription = "Volver a conectar ahora con el servicio local",
        DiagnosticsButton = "Abrir la carpeta de diagnósticos",
        DiagnosticsButtonDescription = "Abrir la carpeta de archivos de diagnóstico locales",
        ExitButton = "Salir",
        ExitButtonDescription = "Salir del Administrador de recursos",
        BackendUnavailableTitle = "El servicio local no está disponible temporalmente",
        BackendUnavailableDetail = "El Administrador de recursos sigue intentando conectarse en segundo plano.",
        FrontendLoadingTitle = "Cargando la interfaz",
        FrontendLoadingDetail = "El servicio local está verificado; cargando la interfaz de la aplicación.",
        FrontendUnavailableTitle = "La interfaz de la aplicación no está disponible temporalmente",
        FrontendUnavailableDetail = "El servicio local sigue en ejecución, por lo que la interfaz se puede volver a cargar.",

        StatusConnecting = "Conectando con el servicio local",
        StatusReconnecting = "Reconectando con el servicio local",
        StatusBackendUnavailable = "Servicio local no disponible",
        StatusShuttingDown = "Saliendo",
        StatusBackendReady = "Servicio local listo",
        StatusFrontendLoading = "Cargando la interfaz",
        StatusReady = "Listo",
        StatusFrontendUnavailable = "Interfaz no disponible",
        ActionReconnectBackend = "Volver a conectar con el servicio local",
        ActionReloadFrontend = "Volver a cargar la interfaz",

        BackendProcessExitedFormat = "El proceso del servicio local terminó con el código {0}.",
        BackendHealthProbeFailedFormat = "El servicio local ha fallado {0} comprobaciones de estado consecutivas.",
        BackendStartFailedFormat = "No se pudo iniciar el servicio local: {0}",
        BackendLifecycleMonitorFailedFormat = "Error al supervisar el ciclo de vida del servicio local: {0}",
        BackendHttpErrorFormat = "El servicio local devolvió HTTP {0}.",
        BackendEntryMissing = "Falta el punto de entrada del servicio backend local en la imagen final.",
        BackendNotOwned = "El servicio backend local no está listo; la interfaz nativa no tiene permiso para iniciar el servicio. Inicie o repare ResourceManager.Service.",
        BackendPipelineNotReady = "La canalización de solicitudes del servicio local no está lista.",
        NoProcessToTerminate = "No hay ningún proceso que finalizar.",
        TerminateRequestCompleted = "La solicitud de finalización del proceso se ha completado.",

        WebViewInitializationFailed = "No se pudo inicializar la interfaz incrustada. Abra la carpeta de diagnósticos para ver los detalles.",
        FrontendIdentityVerificationFailed = "Error al verificar la identidad de los recursos del front-end. Abra la carpeta de diagnósticos para ver los detalles.",
        FrontendLoadFailed = "No se pudo cargar la interfaz local desde el servicio verificado.",
        WebViewRuntimeMissing = "No se encontró un entorno de ejecución de WebView2 compatible. Instale el entorno compartido e inténtelo de nuevo.",

        TrayBackendStatusConnecting = "Servicio local: conectando",
        TrayStatusFormat = "Estado: {0}",
        TrayOpen = "Abrir",
        TrayReconnectBackend = "Volver a conectar con el servicio local",
        TrayExit = "Salir",
        BackendUnavailableBalloonTitle = "Servicio local no disponible",
        NoVerifiedSession = "Todavía no se ha establecido una sesión verificable con el servicio local.",
        DiagnosticsOpenFailed = "No se pudo abrir la carpeta de diagnósticos.",

        SelectSoftwareRootFolder = "Seleccionar la carpeta raíz de la aplicación",

        ForceTerminateHotkeyBalloonTitle = "Tecla de acceso rápido de finalización forzada",
        NoForegroundProcessToTerminate = "No hay ninguna aplicación en primer plano o que no responda que finalizar.",
        BackendUnavailableNoTerminate = "El servicio local no está disponible, por lo que no se finalizó ningún proceso.",
        ForceTerminateFailed = "La finalización forzada ha fallado. Inténtelo de nuevo más tarde.",
        ForceTerminateHotkeyDisabled = "La tecla de acceso rápido de finalización forzada está deshabilitada: debe combinar Ctrl, Alt o Win con una tecla no modificadora.",
        ForceTerminateHotkeyEnableFailed = "No se pudo habilitar la tecla de acceso rápido de finalización forzada. Compruebe la combinación de teclas e inténtelo de nuevo.",
        HotkeyNeedsAtLeastOneKey = "Una tecla de acceso rápido necesita al menos una tecla.",
        ForceTerminateHotkeyNeedsModifier = "La tecla de acceso rápido de finalización forzada debe combinar Ctrl, Alt o Win con una tecla no modificadora.",
        KeyboardHookRegisterFailed = "No se pudo registrar el enlace global de teclado personalizado.",
        MouseHookRegisterFailed = "No se pudo registrar el enlace global de ratón personalizado.",

        TaskManagerHotkeyReplacement = "Sustitución del acceso rápido del Administrador de tareas",
        TaskManagerHotkeyEnableFailed = "No se pudo habilitar el acceso rápido del Administrador de tareas. Inténtelo de nuevo más tarde.",
        TaskManagerHotkeyDisableFailed = "No se pudo deshabilitar el acceso rápido del Administrador de tareas. Inténtelo de nuevo más tarde.",
        TaskManagerHotkeyForeignSetting = "Se detectó otra configuración de acceso rápido del Administrador de tareas y se ha conservado sin cambios.",
        TaskManagerHotkeyNeedsAdminToEnable = "Se necesitan permisos de administrador para tomar el control del acceso rápido del Administrador de tareas mientras el Administrador de recursos no se está ejecutando.",
        TaskManagerHotkeyNeedsAdminToDisable = "Se necesitan permisos de administrador para desactivar por completo la sustitución del acceso rápido del Administrador de tareas.",
        TaskManagerHotkeyEnabled = "El acceso rápido del Administrador de tareas está habilitado.",
        TaskManagerHotkeyDisabled = "El acceso rápido del Administrador de tareas está deshabilitado.",
        TaskManagerShortcutHookRegisterFailed = "No se pudo registrar el enlace del acceso rápido Ctrl+Mayús+Esc.",
        TaskManagerShortcutHookRemoveFailed = "No se pudo quitar el enlace del acceso rápido Ctrl+Mayús+Esc."
    };
}
