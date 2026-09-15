namespace ResourceManager.NativeUi.Localization.Texts;

internal static class JaJp
{
    public static readonly NativeText Text = new()
    {
        AppName = "リソース マネージャー",
        WindowTitleFormat = "リソース マネージャー - {0}",
        TrayTooltipFormat = "リソース マネージャー - {0}",

        AvailabilityPanelName = "ローカル サービスの状態",
        ConnectingTitle = "ローカル サービスに接続しています",
        ReconnectingTitle = "ローカル サービスに再接続しています",
        ConnectingDetail = "サービスの ID を検証してセッションを確立しています。",
        RetryButton = "再試行",
        RetryButtonDescription = "ローカル サービスにすぐ再接続します",
        DiagnosticsButton = "診断フォルダーを開く",
        DiagnosticsButtonDescription = "ローカル診断ファイルのフォルダーを開きます",
        ExitButton = "終了",
        ExitButtonDescription = "リソース マネージャーを終了します",
        BackendUnavailableTitle = "ローカル サービスを一時的に利用できません",
        BackendUnavailableDetail = "リソース マネージャーはバックグラウンドで接続を試行し続けます。",
        FrontendLoadingTitle = "画面を読み込んでいます",
        FrontendLoadingDetail = "ローカル サービスの検証が完了しました。アプリ画面を読み込んでいます。",
        FrontendUnavailableTitle = "アプリ画面を一時的に利用できません",
        FrontendUnavailableDetail = "ローカル サービスは動作中のため、画面を再読み込みできます。",

        StatusConnecting = "ローカル サービスに接続しています",
        StatusReconnecting = "ローカル サービスに再接続しています",
        StatusBackendUnavailable = "ローカル サービスを利用できません",
        StatusShuttingDown = "終了しています",
        StatusBackendReady = "ローカル サービスの準備完了",
        StatusFrontendLoading = "画面を読み込んでいます",
        StatusReady = "準備完了",
        StatusFrontendUnavailable = "画面を利用できません",
        ActionReconnectBackend = "ローカル サービスに再接続",
        ActionReloadFrontend = "画面を再読み込み",

        BackendProcessExitedFormat = "ローカル サービスのプロセスが終了コード {0} で終了しました。",
        BackendHealthProbeFailedFormat = "ローカル サービスのヘルス チェックが {0} 回連続で失敗しました。",
        BackendStartFailedFormat = "ローカル サービスの起動に失敗しました: {0}",
        BackendLifecycleMonitorFailedFormat = "ローカル サービスのライフサイクル監視に失敗しました: {0}",
        BackendHttpErrorFormat = "ローカル サービスが HTTP {0} を返しました。",
        BackendEntryMissing = "最終イメージにローカル バックエンド サービスのエントリ ポイントがありません。",
        BackendNotOwned = "ローカル バックエンド サービスの準備ができていません。ネイティブ UI はサービスの起動権限を持ちません。ResourceManager.Service を起動または修復してください。",
        BackendPipelineNotReady = "ローカル サービスの要求パイプラインの準備ができていません。",
        NoProcessToTerminate = "終了できるプロセスがありません。",
        TerminateRequestCompleted = "プロセス終了の要求が完了しました。",

        WebViewInitializationFailed = "埋め込み画面の初期化に失敗しました。診断フォルダーで詳細を確認できます。",
        FrontendIdentityVerificationFailed = "フロントエンド資産の ID 検証に失敗しました。診断フォルダーで詳細を確認できます。",
        FrontendLoadFailed = "検証済みサービスからローカル画面を読み込めませんでした。",
        WebViewRuntimeMissing = "互換性のある WebView2 ランタイムが見つかりません。共有ランタイムをインストールして再試行してください。",

        TrayBackendStatusConnecting = "ローカル サービス: 接続中",
        TrayStatusFormat = "状態: {0}",
        TrayOpen = "開く",
        TrayReconnectBackend = "ローカル サービスに再接続",
        TrayExit = "終了",
        BackendUnavailableBalloonTitle = "ローカル サービスを利用できません",
        NoVerifiedSession = "検証可能なローカル サービス セッションがまだ確立されていません。",
        DiagnosticsOpenFailed = "診断フォルダーを開けませんでした。",

        SelectSoftwareRootFolder = "アプリのルート フォルダーを選択",

        ForceTerminateHotkeyBalloonTitle = "強制終了ホットキー",
        NoForegroundProcessToTerminate = "終了できるフォアグラウンドまたは応答なしのアプリがありません。",
        BackendUnavailableNoTerminate = "ローカル サービスを利用できないため、プロセスは終了しませんでした。",
        ForceTerminateFailed = "強制終了に失敗しました。しばらくしてから再試行してください。",
        ForceTerminateHotkeyDisabled = "強制終了ホットキーは無効です: Ctrl、Alt、Win のいずれかの修飾キーと修飾キー以外のキーの両方が必要です。",
        ForceTerminateHotkeyEnableFailed = "強制終了ホットキーを有効にできませんでした。キーの設定を確認して再試行してください。",
        HotkeyNeedsAtLeastOneKey = "ホットキーには少なくとも 1 つのキーが必要です。",
        ForceTerminateHotkeyNeedsModifier = "強制終了ホットキーには Ctrl、Alt、Win のいずれかの修飾キーと修飾キー以外のキーの両方が必要です。",
        KeyboardHookRegisterFailed = "カスタム グローバル キーボード ホットキー フックを登録できません。",
        MouseHookRegisterFailed = "カスタム グローバル マウス ホットキー フックを登録できません。",

        TaskManagerHotkeyReplacement = "タスク マネージャー ホットキーの置き換え",
        TaskManagerHotkeyEnableFailed = "タスク マネージャー ホットキーを有効にできませんでした。しばらくしてから再試行してください。",
        TaskManagerHotkeyDisableFailed = "タスク マネージャー ホットキーを無効にできませんでした。しばらくしてから再試行してください。",
        TaskManagerHotkeyForeignSetting = "別のタスク マネージャー ホットキー設定が検出されたため、既存の設定を保持しました。",
        TaskManagerHotkeyNeedsAdminToEnable = "リソース マネージャーが実行されていないときにタスク マネージャー ホットキーを引き継ぐには管理者権限が必要です。",
        TaskManagerHotkeyNeedsAdminToDisable = "タスク マネージャー ホットキーの置き換えを完全に無効にするには管理者権限が必要です。",
        TaskManagerHotkeyEnabled = "タスク マネージャー ホットキーを有効にしました。",
        TaskManagerHotkeyDisabled = "タスク マネージャー ホットキーを無効にしました。",
        TaskManagerShortcutHookRegisterFailed = "Ctrl+Shift+Esc ホットキー フックを登録できません。",
        TaskManagerShortcutHookRemoveFailed = "Ctrl+Shift+Esc ホットキー フックを削除できません。"
    };
}
