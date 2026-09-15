namespace ResourceManager.NativeUi.Localization.Texts;

internal static class RuRu
{
    public static readonly NativeText Text = new()
    {
        AppName = "Диспетчер ресурсов",
        WindowTitleFormat = "Диспетчер ресурсов - {0}",
        TrayTooltipFormat = "Диспетчер ресурсов - {0}",

        AvailabilityPanelName = "Состояние локальной службы",
        ConnectingTitle = "Подключение к локальной службе",
        ReconnectingTitle = "Повторное подключение к локальной службе",
        ConnectingDetail = "Проверка подлинности службы и установка сеанса.",
        RetryButton = "Повторить",
        RetryButtonDescription = "Немедленно подключиться к локальной службе заново",
        DiagnosticsButton = "Открыть папку диагностики",
        DiagnosticsButtonDescription = "Открыть папку с локальными файлами диагностики",
        ExitButton = "Выход",
        ExitButtonDescription = "Выйти из диспетчера ресурсов",
        BackendUnavailableTitle = "Локальная служба временно недоступна",
        BackendUnavailableDetail = "Диспетчер ресурсов продолжает попытки подключения в фоновом режиме.",
        FrontendLoadingTitle = "Загрузка интерфейса",
        FrontendLoadingDetail = "Подлинность локальной службы подтверждена, загружается интерфейс приложения.",
        FrontendUnavailableTitle = "Интерфейс приложения временно недоступен",
        FrontendUnavailableDetail = "Локальная служба продолжает работать, интерфейс можно перезагрузить.",

        StatusConnecting = "Подключение к локальной службе",
        StatusReconnecting = "Повторное подключение к локальной службе",
        StatusBackendUnavailable = "Локальная служба недоступна",
        StatusShuttingDown = "Завершение работы",
        StatusBackendReady = "Локальная служба готова",
        StatusFrontendLoading = "Загрузка интерфейса",
        StatusReady = "Готово",
        StatusFrontendUnavailable = "Интерфейс недоступен",
        ActionReconnectBackend = "Подключиться к локальной службе заново",
        ActionReloadFrontend = "Перезагрузить интерфейс",

        BackendProcessExitedFormat = "Процесс локальной службы завершился с кодом {0}.",
        BackendHealthProbeFailedFormat = "Локальная служба не прошла {0} проверок работоспособности подряд.",
        BackendStartFailedFormat = "Не удалось запустить локальную службу: {0}",
        BackendLifecycleMonitorFailedFormat = "Сбой отслеживания жизненного цикла локальной службы: {0}",
        BackendHttpErrorFormat = "Локальная служба вернула HTTP {0}.",
        BackendEntryMissing = "В итоговом образе отсутствует точка входа локальной серверной службы.",
        BackendNotOwned = "Локальная серверная служба не готова; у собственного интерфейса нет прав на её запуск. Запустите или восстановите ResourceManager.Service.",
        BackendPipelineNotReady = "Конвейер запросов локальной службы не готов.",
        NoProcessToTerminate = "Нет процессов для завершения.",
        TerminateRequestCompleted = "Запрос на завершение процесса выполнен.",

        WebViewInitializationFailed = "Не удалось инициализировать встроенный интерфейс. Подробности можно посмотреть в папке диагностики.",
        FrontendIdentityVerificationFailed = "Не удалось проверить подлинность ресурсов интерфейса. Подробности можно посмотреть в папке диагностики.",
        FrontendLoadFailed = "Не удалось загрузить локальный интерфейс из проверенной службы.",
        WebViewRuntimeMissing = "Совместимая среда выполнения WebView2 не найдена. Установите общую среду выполнения и повторите попытку.",

        TrayBackendStatusConnecting = "Локальная служба: подключение",
        TrayStatusFormat = "Состояние: {0}",
        TrayOpen = "Открыть",
        TrayReconnectBackend = "Подключиться к локальной службе заново",
        TrayExit = "Выход",
        BackendUnavailableBalloonTitle = "Локальная служба недоступна",
        NoVerifiedSession = "Проверяемый сеанс с локальной службой ещё не установлен.",
        DiagnosticsOpenFailed = "Не удалось открыть папку диагностики.",

        SelectSoftwareRootFolder = "Выберите корневую папку приложения",

        ForceTerminateHotkeyBalloonTitle = "Сочетание клавиш принудительного завершения",
        NoForegroundProcessToTerminate = "Нет активного или не отвечающего приложения для завершения.",
        BackendUnavailableNoTerminate = "Локальная служба недоступна, процесс не был завершён.",
        ForceTerminateFailed = "Не удалось выполнить принудительное завершение. Повторите попытку позже.",
        ForceTerminateHotkeyDisabled = "Сочетание клавиш принудительного завершения отключено: оно должно включать Ctrl, Alt или Win вместе с обычной клавишей.",
        ForceTerminateHotkeyEnableFailed = "Не удалось включить сочетание клавиш принудительного завершения. Проверьте комбинацию клавиш и повторите попытку.",
        HotkeyNeedsAtLeastOneKey = "Для сочетания клавиш нужна хотя бы одна клавиша.",
        ForceTerminateHotkeyNeedsModifier = "Сочетание клавиш принудительного завершения должно включать Ctrl, Alt или Win вместе с обычной клавишей.",
        KeyboardHookRegisterFailed = "Не удалось зарегистрировать пользовательский глобальный перехватчик клавиатуры.",
        MouseHookRegisterFailed = "Не удалось зарегистрировать пользовательский глобальный перехватчик мыши.",

        TaskManagerHotkeyReplacement = "Замена сочетания клавиш диспетчера задач",
        TaskManagerHotkeyEnableFailed = "Не удалось включить сочетание клавиш диспетчера задач. Повторите попытку позже.",
        TaskManagerHotkeyDisableFailed = "Не удалось отключить сочетание клавиш диспетчера задач. Повторите попытку позже.",
        TaskManagerHotkeyForeignSetting = "Обнаружена другая настройка сочетания клавиш диспетчера задач, она оставлена без изменений.",
        TaskManagerHotkeyNeedsAdminToEnable = "Чтобы перехватывать сочетание клавиш диспетчера задач, когда диспетчер ресурсов не запущен, нужны права администратора.",
        TaskManagerHotkeyNeedsAdminToDisable = "Чтобы полностью отключить замену сочетания клавиш диспетчера задач, нужны права администратора.",
        TaskManagerHotkeyEnabled = "Сочетание клавиш диспетчера задач включено.",
        TaskManagerHotkeyDisabled = "Сочетание клавиш диспетчера задач отключено.",
        TaskManagerShortcutHookRegisterFailed = "Не удалось зарегистрировать перехватчик сочетания клавиш Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Не удалось удалить перехватчик сочетания клавиш Ctrl+Shift+Esc."
    };
}
