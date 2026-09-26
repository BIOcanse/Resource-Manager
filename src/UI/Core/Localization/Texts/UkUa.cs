namespace ResourceManager.NativeUi.Localization.Texts;

internal static class UkUa
{
    public static readonly NativeText Text = new()
    {
        AppName = "Диспетчер ресурсів",
        WindowTitleFormat = "Диспетчер ресурсів - {0}",
        TrayTooltipFormat = "Диспетчер ресурсів - {0}",

        AvailabilityPanelName = "Стан локальної служби",
        ConnectingTitle = "Підключення до локальної служби",
        ReconnectingTitle = "Повторне підключення до локальної служби",
        ConnectingDetail = "Перевірка ідентичності служби та встановлення сеансу.",
        RetryButton = "Повторити",
        RetryButtonDescription = "Негайно підключитися до локальної служби знову",
        DiagnosticsButton = "Відкрити папку діагностики",
        DiagnosticsButtonDescription = "Відкрити папку локальних файлів діагностики",
        ExitButton = "Вихід",
        ExitButtonDescription = "Вийти з диспетчера ресурсів",
        BackendUnavailableTitle = "Локальна служба тимчасово недоступна",
        BackendUnavailableDetail = "Диспетчер ресурсів продовжує спроби підключення у фоновому режимі.",
        FrontendLoadingTitle = "Завантаження інтерфейсу",
        FrontendLoadingDetail = "Локальну службу перевірено, триває завантаження інтерфейсу застосунку.",
        FrontendUnavailableTitle = "Інтерфейс застосунку тимчасово недоступний",
        FrontendUnavailableDetail = "Локальна служба продовжує працювати, тож інтерфейс можна перезавантажити.",

        StatusConnecting = "Підключення до локальної служби",
        StatusReconnecting = "Повторне підключення до локальної служби",
        StatusBackendUnavailable = "Локальна служба недоступна",
        StatusShuttingDown = "Завершення роботи",
        StatusBackendReady = "Локальна служба готова",
        StatusFrontendLoading = "Завантаження інтерфейсу",
        StatusReady = "Готово",
        StatusFrontendUnavailable = "Інтерфейс недоступний",
        ActionReconnectBackend = "Підключитися до локальної служби знову",
        ActionReloadFrontend = "Перезавантажити інтерфейс",

        BackendProcessExitedFormat = "Процес локальної служби завершився з кодом {0}.",
        BackendHealthProbeFailedFormat = "Локальна служба не пройшла {0} перевірок працездатності поспіль.",
        BackendStartFailedFormat = "Не вдалося запустити локальну службу: {0}",
        BackendLifecycleMonitorFailedFormat = "Збій моніторингу життєвого циклу локальної служби: {0}",
        BackendHttpErrorFormat = "Локальна служба повернула HTTP {0}.",
        BackendEntryMissing = "У підсумковому образі відсутня точка входу локальної серверної служби.",
        BackendNotOwned = "Локальна серверна служба не готова; власний інтерфейс не має прав на її запуск. Запустіть або відновіть ResourceManager.Service.",
        BackendPipelineNotReady = "Конвеєр запитів локальної служби не готовий.",
        NoProcessToTerminate = "Немає процесів для завершення.",
        TerminateRequestCompleted = "Запит на завершення процесу виконано.",

        WebViewInitializationFailed = "Не вдалося ініціалізувати вбудований інтерфейс. Подробиці можна переглянути в папці діагностики.",
        FrontendIdentityVerificationFailed = "Не вдалося перевірити ідентичність ресурсів інтерфейсу. Подробиці можна переглянути в папці діагностики.",
        FrontendLoadFailed = "Не вдалося завантажити локальний інтерфейс із перевіреної служби.",
        WebViewRuntimeMissing = "Сумісного середовища виконання WebView2 не знайдено. Установіть спільне середовище виконання та повторіть спробу.",

        TrayBackendStatusConnecting = "Локальна служба: підключення",
        TrayStatusFormat = "Стан: {0}",
        TrayOpen = "Відкрити",
        TrayReconnectBackend = "Підключитися до локальної служби знову",
        TrayExit = "Вихід",
        BackendUnavailableBalloonTitle = "Локальна служба недоступна",
        NoVerifiedSession = "Перевірюваний сеанс із локальною службою ще не встановлено.",
        DiagnosticsOpenFailed = "Не вдалося відкрити папку діагностики.",

        SelectSoftwareRootFolder = "Виберіть кореневу папку застосунку",

        ForceTerminateHotkeyBalloonTitle = "Сполучення клавіш примусового завершення",
        NoForegroundProcessToTerminate = "Немає активного застосунку або застосунку, що не відповідає, для завершення.",
        BackendUnavailableNoTerminate = "Локальна служба недоступна, процес не було завершено.",
        ForceTerminateFailed = "Не вдалося виконати примусове завершення. Повторіть спробу пізніше.",
        ForceTerminateHotkeyDisabled = "Сполучення клавіш примусового завершення вимкнено: воно має містити Ctrl, Alt або Win разом зі звичайною клавішею.",
        ForceTerminateHotkeyEnableFailed = "Не вдалося увімкнути сполучення клавіш примусового завершення. Перевірте комбінацію клавіш і повторіть спробу.",
        HotkeyNeedsAtLeastOneKey = "Для сполучення клавіш потрібна щонайменше одна клавіша.",
        ForceTerminateHotkeyNeedsModifier = "Сполучення клавіш примусового завершення має містити Ctrl, Alt або Win разом зі звичайною клавішею.",
        KeyboardHookRegisterFailed = "Не вдалося зареєструвати користувацький глобальний перехоплювач клавіатури.",
        MouseHookRegisterFailed = "Не вдалося зареєструвати користувацький глобальний перехоплювач миші.",

        TaskManagerHotkeyReplacement = "Заміна сполучення клавіш диспетчера завдань",
        TaskManagerHotkeyEnableFailed = "Не вдалося увімкнути сполучення клавіш диспетчера завдань. Повторіть спробу пізніше.",
        TaskManagerHotkeyDisableFailed = "Не вдалося вимкнути сполучення клавіш диспетчера завдань. Повторіть спробу пізніше.",
        TaskManagerHotkeyForeignSetting = "Виявлено інше налаштування сполучення клавіш диспетчера завдань, його залишено без змін.",
        TaskManagerHotkeyNeedsAdminToEnable = "Щоб перехоплювати сполучення клавіш диспетчера завдань, коли диспетчер ресурсів не запущено, потрібні права адміністратора.",
        TaskManagerHotkeyNeedsAdminToDisable = "Щоб повністю вимкнути заміну сполучення клавіш диспетчера завдань, потрібні права адміністратора.",
        TaskManagerHotkeyEnabled = "Сполучення клавіш диспетчера завдань увімкнено.",
        TaskManagerHotkeyDisabled = "Сполучення клавіш диспетчера завдань вимкнено.",
        TaskManagerShortcutHookRegisterFailed = "Не вдалося зареєструвати перехоплювач сполучення клавіш Ctrl+Shift+Esc.",
        TaskManagerShortcutHookRemoveFailed = "Не вдалося видалити перехоплювач сполучення клавіш Ctrl+Shift+Esc."
    };
}
