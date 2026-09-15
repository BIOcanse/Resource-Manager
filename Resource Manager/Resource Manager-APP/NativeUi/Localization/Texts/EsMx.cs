namespace ResourceManager.NativeUi.Localization.Texts;

internal static class EsMx
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
        FrontendUnavailableDetail = "El servicio local sigue en ejecución, así que la interfaz se puede volver a cargar.",

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
        BackendHealthProbeFailedFormat = "El servicio local falló {0} comprobaciones de estado consecutivas.",
        BackendStartFailedFormat = "No se pudo iniciar el servicio local: {0}",
        BackendLifecycleMonitorFailedFormat = "Error al monitorear el ciclo de vida del servicio local: {0}",
        BackendHttpErrorFormat = "El servicio local devolvió HTTP {0}.",
        BackendEntryMissing = "Falta el punto de entrada del servicio backend local en la imagen final.",
        BackendNotOwned = "El servicio backend local no está listo; la interfaz nativa no tiene permiso para iniciar el servicio. Inicia o repara ResourceManager.Service.",
        BackendPipelineNotReady = "La canalización de solicitudes del servicio local no está lista.",
        NoProcessToTerminate = "No hay ningún proceso que terminar.",
        TerminateRequestCompleted = "La solicitud para terminar el proceso se completó.",

        WebViewInitializationFailed = "No se pudo inicializar la interfaz incrustada. Abre la carpeta de diagnósticos para ver los detalles.",
        FrontendIdentityVerificationFailed = "Error al verificar la identidad de los recursos del front-end. Abre la carpeta de diagnósticos para ver los detalles.",
        FrontendLoadFailed = "No se pudo cargar la interfaz local desde el servicio verificado.",
        WebViewRuntimeMissing = "No se encontró un entorno de ejecución de WebView2 compatible. Instala el entorno compartido e inténtalo de nuevo.",

        TrayBackendStatusConnecting = "Servicio local: conectando",
        TrayStatusFormat = "Estado: {0}",
        TrayOpen = "Abrir",
        TrayReconnectBackend = "Volver a conectar con el servicio local",
        TrayExit = "Salir",
        BackendUnavailableBalloonTitle = "Servicio local no disponible",
        NoVerifiedSession = "Todavía no se ha establecido una sesión verificable con el servicio local.",
        DiagnosticsOpenFailed = "No se pudo abrir la carpeta de diagnósticos.",

        SelectSoftwareRootFolder = "Seleccionar la carpeta raíz de la aplicación",

        ForceTerminateHotkeyBalloonTitle = "Atajo de terminación forzada",
        NoForegroundProcessToTerminate = "No hay ninguna aplicación en primer plano o que no responda que terminar.",
        BackendUnavailableNoTerminate = "El servicio local no está disponible, así que no se terminó ningún proceso.",
        ForceTerminateFailed = "La terminación forzada falló. Inténtalo de nuevo más tarde.",
        ForceTerminateHotkeyDisabled = "El atajo de terminación forzada está deshabilitado: debe combinar Ctrl, Alt o Win con una tecla no modificadora.",
        ForceTerminateHotkeyEnableFailed = "No se pudo habilitar el atajo de terminación forzada. Revisa la combinación de teclas e inténtalo de nuevo.",
        HotkeyNeedsAtLeastOneKey = "Un atajo necesita al menos una tecla.",
        ForceTerminateHotkeyNeedsModifier = "El atajo de terminación forzada debe combinar Ctrl, Alt o Win con una tecla no modificadora.",
        KeyboardHookRegisterFailed = "No se pudo registrar el enlace global de teclado personalizado.",
        MouseHookRegisterFailed = "No se pudo registrar el enlace global de mouse personalizado.",

        TaskManagerHotkeyReplacement = "Reemplazo del atajo del Administrador de tareas",
        TaskManagerHotkeyEnableFailed = "No se pudo habilitar el atajo del Administrador de tareas. Inténtalo de nuevo más tarde.",
        TaskManagerHotkeyDisableFailed = "No se pudo deshabilitar el atajo del Administrador de tareas. Inténtalo de nuevo más tarde.",
        TaskManagerHotkeyForeignSetting = "Se detectó otra configuración del atajo del Administrador de tareas y se conservó sin cambios.",
        TaskManagerHotkeyNeedsAdminToEnable = "Se necesitan permisos de administrador para tomar el control del atajo del Administrador de tareas mientras el Administrador de recursos no se está ejecutando.",
        TaskManagerHotkeyNeedsAdminToDisable = "Se necesitan permisos de administrador para desactivar por completo el reemplazo del atajo del Administrador de tareas.",
        TaskManagerHotkeyEnabled = "El atajo del Administrador de tareas está habilitado.",
        TaskManagerHotkeyDisabled = "El atajo del Administrador de tareas está deshabilitado.",
        TaskManagerShortcutHookRegisterFailed = "No se pudo registrar el enlace del atajo Ctrl+Mayús+Esc.",
        TaskManagerShortcutHookRemoveFailed = "No se pudo quitar el enlace del atajo Ctrl+Mayús+Esc."
    };
}
