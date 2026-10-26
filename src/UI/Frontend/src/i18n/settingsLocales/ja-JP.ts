import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("ja-JP", {
  "updates": {
    "modeTitle": "更新方式",
    "modeDescription": "オフでは自動確認を行いません。手動確認と手動でのバージョン選択はどの方式でも使えます。",
    "modeOptions": [{ id: "off", label: "オフ", description: "自動での確認・ダウンロード・インストールを行いません。" }, { id: "notify", label: "確認して通知のみ", description: "自動で確認し、新しいバージョンを通知します。ダウンロードはしません。" }, { id: "automatic", label: "自動でダウンロードして適用", description: "自動で確認・ダウンロード・検証し、画面を閉じた後に適用します。今すぐ適用も選べます。" }],
    "periodicTitle": "定期確認",
    "periodicDescription": "起動時の確認の後、この間隔で再確認します。",
    "internetNote": "自動確認はインターネットに接続できるまで待ちます。間隔は接続中の時間だけを数えます。",
    "offReason": "更新方式がオフのため自動確認は行われず、この 2 項目は効きません。値は保持されます。",
    "intervalTitle": "確認間隔（時間）",
    "intervalLabel": (minimum: number, maximum: number) => `間隔（時間、${minimum}–${maximum}）`,
    "inAppNoticeTitle": "画面内で新バージョンを通知",
    "inAppNoticeDescription": "画面の実行中に新しいバージョンが見つかると 1 回通知します。同じバージョンは再通知しません。",
    "accessRequestNoticeTitle": "新しいアクセス要求のシステム通知",
    "accessRequestNoticeDescription": "画面が表示されていないときに、承認待ちの新しい信頼済みアクセス要求を受信すると Windows 通知を表示します。",
    "systemNoticeTitle": "Windows のシステム通知",
    "systemNoticeDescription": "アプリが表示されていないときにシステム通知を 1 回送ります。このスイッチは設定を保存するだけで、実行はデスクトップシェル（トレイ）が行います。",
    "manualCheckNote": "手動確認はインターネットを待たずにすぐ実行され、どの方式でも使えます。",
    "waitingForInternet": "インターネット接続を待っています。接続されるとすぐ確認します。",
    "availableVersion": "見つかった新しいバージョン：",
    "nextCheck": "次回の確認：",
    "nextCheckAfterInternet": "接続後に計測開始",
    "nextCheckOff": "無効",
    "connectedElapsed": "前回の確認からの接続時間：",
    "noticeTitle": "新しいバージョンがあります",
    "noticeMessage": (version: string) => `Resource Manager ${version} に更新できます。`,
    "noticeReadyMessage": (version: string) => `Resource Manager ${version} はダウンロードと検証が済み、今すぐ適用できます。`,
    "noticeOpenUpdates": "更新とバージョンを開く",
    "installedVersionTitle": "インストール済みバージョン",
    "unknownVersion": "不明",
    "checking": "確認中…",
    "checkForUpdates": "更新を確認",
    "historyIncomplete": "リリース履歴の読み込みが完了していません。",
    "historyStale": "以前に読み込んだリリース一覧を表示しています。接続を復旧して再試行してください。",
    "versionsTitle": "製品バージョン",
    "versionsDescription": "新しい順に表示します。各系列では初期状態で最新リリースを表示します。",
    "lastChecked": "最終確認：",
    "selectedTarget": "選択したバージョン：",
    "prepare": "選択した更新を準備",
    "confirmPrepare": (version: string) => `バージョン ${version} を準備しますか？パッケージを検証し、すべてのデスクトップ画面の終了を待ってサービスを停止します。データを保持・移行し、新しいバージョンを検証します。失敗した場合は以前のバージョンに戻します。`,
    "prepareFailed": "更新を準備できません。",
    "waitingForExit": "更新の準備ができました。適用するには、トレイからすべての Resource Manager デスクトップ画面を終了してください。",
    "stageLabel": "更新状態",
    "unknownStage": "不明な更新状態",
    "stages": {
      "idle": "待機中",
      "checking": "更新を確認中",
      "downloading": "パッケージをダウンロード中",
      "verifying": "パッケージを検証中",
      "waitingForExit": "デスクトップ画面の終了を待機中",
      "completed": "更新完了",
      "error": "更新失敗"
    },
    "otherVersions": "その他のバージョン",
    "installedUnknown": "インストール済みバージョンが不明なため、更新方向を確認できません。",
    "stable": "安定版",
    "preview": "プレビュー版",
    "noLatest": "最新リリースはありません。",
    "noStable": "安定版リリースはありません。",
    "latest": "最新リリース",
    "latestStable": "最新の安定版",
    "verified": "検証済みリリース",
    "unavailableReasons": {
      "installedVersionUnknown": "現在のインストール済みバージョンが不明なため、更新の選択は無効です。",
      "notNewer": "インストール済みバージョンより新しいリリースのみ選択できます。"
    }
  },
  "navigationLabel": "設定の分類",
  "sections": {
    "performance": "パフォーマンス",
    "appearance": "外観",
    "systemIntegration": "システム統合",
    "updates": "更新とバージョン",
    "debug": "デバッグ",
    "credits": "クレジット"
  },
  "saveState": {
    "saving": "保存中",
    "saved": "保存済み",
    "constrained": "保存済み。現在利用できない動作オプションは制限されました",
    "partial": "保存済みですが、一部は未適用です",
    "error": "保存に失敗",
    "dirty": "未保存",
    "conflict": "ほかの場所で設定が変更されました。保存前に再読み込みしてください"
  },
  "loadState": {
    "loading": "保存済みの設定を読み込み中…",
    "errorTitle": "設定を読み込めません",
    "errorDescription": "初期値を保存済み設定の代わりに表示・使用していません。ローカルサービスを復旧して再試行してください。",
    "staleTitle": "設定の同期が失われています",
    "staleDescription": "この設定は古い可能性があります。再読み込みに成功するまで変更は送信されません。"
  },
  "actions": {
    "save": "保存",
    "restoreDefaults": "初期値に戻す",
    "retry": "再試行",
    "reload": "再読み込み",
    "reapply": "再適用"
  },
  "performance": {
    "preciseGpuPlacementTitle": "GPU の正確な選択",
    "preciseGpuPlacementDescription": "対応する描画プロセスで GPU を正確に指定できます。実行中の切り替えはプロセスの互換性に依存し、アプリごとに設定できます。",
    "preciseGpuPlacementModeOptions": {
      "basic": {
        "label": "オフ",
        "description": "Windows が対応する GPU の優先設定のみ使用します。"
      },
      "precise": {
        "label": "オン",
        "description": "ソフトウェアの使用 GPU を正確に指定できるようにします。"
      }
    },
    "automaticMemoryCleanupTitle": "緊急クリーンアップのしきい値",
    "automaticMemoryCleanupDescription": "バックグラウンドのクリーンアップはリソースの負荷に応じて段階的に行われます。物理メモリまたはコミット容量の残量がこの値を下回ると、緊急クリーンアップを行います。最適化モードにメモリ調整が含まれる場合のみ有効です。",
    "physicalMemoryAutomaticCleanupLabel": "物理メモリ空き率 %",
    "virtualMemoryAutomaticCleanupLabel": "コミット余裕率 %",
    "memoryOptimizationTargetTitle": "メモリ最適化の目標",
    "memoryOptimizationTargetDescription": "目標を超えた使用量は、第 1 段階の緩やかな解放を継続します。緊急解放は開始しません。",
    "physicalMemoryOptimizationTargetLabel": "物理メモリ目標使用率 %",
    "virtualMemoryOptimizationTargetLabel": "コミット目標使用率 %",
    "gpuOverflowTitle": "専用 GPU のオーバーフローしきい値",
    "gpuOverflowDescription": "いずれかのしきい値に達すると、スコアの低いバックグラウンドプロセスを専用 GPU から内蔵 GPU に移します。スケジューリングが有効で、内蔵 GPU が利用可能な場合に適用されます。",
    "gpuOverflowUsageLabel": "専用 GPU 使用率 %",
    "gpuOverflowDedicatedMemoryLabel": "専用 GPU メモリ使用率 %",
    "realtimeActionBudgetTitle": "リアルタイム操作の上限",
    "realtimeActionBudgetDescription": "各ラウンドのリアルタイム操作数を固定数、またはそのラウンドで識別された全プロセス数の割合（切り上げ）で制限します。モードを切り替えても両方の値を保持します。",
    "realtimeActionBudgetModeOptions": [{"id":"fixed","label":"固定数"},{"id":"percent","label":"プロセスの割合"}],
    "realtimeActionBudgetCountLabel": "固定の操作数",
    "realtimeActionBudgetPercentLabel": "全プロセスに対する割合 %",
    "refreshCadenceTitle": "情報の更新頻度",
    "refreshCadenceDescription": "監視情報と状態の更新速度を設定します。速くするとリソース使用量が少し増えます。",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "自動",
        "description": "現在の性能モードとウィンドウ状態に合わせて更新頻度を選びます。"
      },
      {
        "id": "preset",
        "label": "プリセットを固定",
        "description": "選択したプリセットの更新頻度を常に使用します。"
      },
      {
        "id": "custom",
        "label": "カスタム",
        "description": "手入力したミリ秒の値を常に使用します。"
      }
    ],
    "refreshCadencePresetLabel": "プリセット",
    "refreshCadenceCustomLabel": "カスタム（ms）",
    "refreshCadencePresetOptions": [
      {
        "id": "responsive",
        "label": "応答重視",
        "description": "前面での操作を優先して、更新を速くします。"
      },
      {
        "id": "balanced",
        "label": "バランス",
        "description": "日常使用に適した更新頻度です。"
      },
      {
        "id": "lowPower",
        "label": "省電力",
        "description": "バックグラウンドの省電力方針です。"
      },
      {
        "id": "quiet",
        "label": "静か",
        "description": "必要な更新を保ちつつ、頻度を最小限にします。"
      }
    ],
    "refreshCadenceItems": {
      "monitor": {
        "label": "監視値とリソースバー",
        "description": "ダッシュボードの現在値とリソースバーを更新します。"
      },
      "resourceTable": {
        "label": "リソース一覧",
        "description": "ソフトウェアとプロセスのリソース一覧を更新します。"
      },
      "localSystem": {
        "label": "ローカルシステム",
        "description": "稼働時間など、低頻度で変化するローカル状態を更新します。"
      }
    }
  },
  "appearance": {
    "cacheDescription": "\u5916\u89B3\u3001\u66F4\u65B0\u9593\u9694\u3001\u30AA\u30FC\u30D0\u30FC\u30EC\u30A4\u306E\u5916\u89B3\u306F\u3053\u306E\u7AEF\u672B\u306B\u30AD\u30E3\u30C3\u30B7\u30E5\u3055\u308C\u307E\u3059\u3002\u30AD\u30E3\u30C3\u30B7\u30E5\u3092\u6D88\u53BB\u3059\u308B\u3068\u65E2\u5B9A\u5024\u306B\u623B\u308A\u307E\u3059\u3002",
    "resetCache": "\u753B\u9762\u8A2D\u5B9A\u3092\u65E2\u5B9A\u5024\u306B\u623B\u3059",
    "uiStyleTitle": "インターフェイス スタイル",
    "uiStyleDescription": "コントロールの角丸、線、影、フォントを変えます。レイアウトは変わりません。スタイルごとに配色があります。",
    "themeTitle": "配色",
    "themeDescription": "現在のスタイルの配色から選びます。「システム」は Windows のライト/ダーク設定に従います。",
    "animationTitle": "アニメーション",
    "animationDescription": "通常のアニメーション、アニメーションなし、または負荷を抑えた簡易表示を選びます。",
    "motionFeelTitle": "操作感",
    "motionFeelDescription": "画面全体のアニメーションの速さとカーブを変えます。ドラッグする部品の収まり方や戻り方も変わります。アニメーションなしを選んでいるときは動きません。",
    "resourceBarFrameRateTitle": "リソースバーのアニメーションのフレームレート",
    "resourceBarFrameRateDescription": "リソースバーの切り替えアニメーションを 1 秒間に何フレーム描くかを決めます。画面に合わせるとディスプレイのリフレッシュレートで描きます。カスタムではフレーム数を抑えて負荷を減らせます。標準アニメーションのときだけ有効です。",
    "resourceBarFrameRateModeOptions": [
      {
        "id": "display",
        "label": "画面に合わせる",
        "description": "画面が更新されるたびに 1 フレーム描きます。"
      },
      {
        "id": "custom",
        "label": "カスタム",
        "description": "1 秒あたり設定したフレーム数（10–240）まで描きます。"
      }
    ],
    "resourceBarFrameRateCustomLabel": "フレームレート（FPS）",
    "barColorTitle": "バーの色",
    "barColorDescription": "ソフトウェア種別ごとの固定色、またはソフトウェアごとの個別色を選びます。",
    "byteUnitTitle": "容量の単位",
    "byteUnitDescription": "1 GiB = 1024 MiB、1 GB = 1000 MB。切り替えると単位名だけでなく数値も換算されます。",
    "readingSourceTitle": "読み取り値のソース",
    "readingSourceDescription": "監視コンソールとデバイスの詳細で、複数のソースがある読み取り値をどう表示するかを選びます。既定ではこのコンピューターで最も正確なソースだけを表示します。利用可能なすべてのソースを表示すると、最も正確なものに「優先」が付きます。スケジューリングは常に最も正確なソースを使い、この設定の影響を受けません。",
    "readingSourceModeOptions": [
      {
        "id": "preferred",
        "label": "最も正確なもののみ",
        "description": "各読み取り値について、このコンピューターで最も正確なソースだけを表示します。"
      },
      {
        "id": "all",
        "label": "利用可能なすべて",
        "description": "各読み取り値の利用可能なソースをすべて表示し、最も正確なものに印を付けます。"
      }
    ],
    "fontSmoothingTitle": "文字のアンチエイリアス",
    "fontSmoothingDescription": "文字の鮮明さと描画負荷を自動調整するか、描画方式を固定します。",
    "languageTitle": "表示言語",
    "languageDescription": "Resource Manager の画面で使用する言語を選択します。「システム」は Windows の表示言語を使用します。",
    "settingsLanguageTitle": "表示言語",
    "settingsLanguageDescription": "トレイとウィンドウタイトルを含む画面全体の言語を変更します。システム設定では Windows の表示言語を使用します。",
    "languageSelectLabel": "言語",
    "motionFeelOptions": {
      "standard": "標準",
      "smooth": "なめらか",
      "crisp": "きびきび"
    },
    "uiStyleOptions": {
      "standard": "標準",
      "soft": "ソフト",
      "outline": "アウトライン",
      "terminal": "ターミナル"
    },
    "themeOptions": {
      "system": "システム",
      "light": "ライト",
      "dark": "ダーク",
      "lowContrast": "低コントラスト",
      "paper": "ペーパー",
      "graphite": "グラファイト",
      "blueprint": "ブループリント",
      "phosphor": "蛍光グリーン",
      "amber": "アンバー",
      "ice": "アイス"
    },
    "animationOptions": {
      "auto": {
        "label": "自動",
        "description": "前面操作中は標準モーションを使い、低電力のバックグラウンド状態では高性能表示に切り替えます"
      },
      "normal": {
        "label": "標準アニメーション",
        "description": "控えめな全体トランジションを有効化します"
      },
      "none": {
        "label": "アニメーションなし",
        "description": "すべてのトランジションとアニメーションを無効化します"
      },
      "ultra": {
        "label": "超高性能",
        "description": "アニメーションと影、ハイライト、ぼかしを無効化し、描画コストを最小化します"
      }
    },
    "barColorOptions": {
      "type": {
        "label": "種別ごとの固定色",
        "description": "システム、対応ソフトウェア、一般ソフトウェアで色分けし、隣接部分に区切りを付けます"
      },
      "distinct": {
        "label": "個別の色",
        "description": "ソフトウェアごとに異なる色を使い、種別の意味や区切りは付けません"
      }
    },
    "byteUnitOptions": {
      "native": {
        "label": "ハードウェアに合わせる",
        "description": "メモリ、VRAM、キャッシュは GiB（容量が 2 のべき乗）、ディスク容量、ファイルサイズ、転送量は GB（メーカー表示に合わせる）"
      },
      "binary": {
        "label": "常に GiB",
        "description": "常に 1024 で割り、KiB / MiB / GiB を表示します"
      },
      "decimal": {
        "label": "常に GB",
        "description": "常に 1000 で割り、kB / MB / GB を表示します"
      }
    },
    "fontSmoothingOptions": {
      "auto": {
        "label": "自動",
        "description": "通常はシステムの ClearType を使い、超高性能モードでは平滑化の負荷を抑えます"
      },
      "system": {
        "label": "システム ClearType",
        "description": "Windows 標準のサブピクセル描画で最も鮮明な文字を表示します"
      },
      "grayscale": {
        "label": "グレースケール",
        "description": "グレースケールのアンチエイリアスです。柔らかな表示で、一部の高 DPI 画面に適しています"
      },
      "disabled": {
        "label": "オフ",
        "description": "追加の文字平滑化を無効にし、字形を直接描画します"
      }
    },
  },
  "systemIntegration": {
    "crashRecoveryTitle": "クラッシュ後に再起動",
    "crashRecoveryDescription": "バックグラウンドサービスがクラッシュした場合に自動で再起動します。通常の停止では再起動しません。",
    "crashRestartDelayTitle": "再起動までの待機時間（秒）",
    "crashRestartCountTitle": "再起動の上限回数",
    "crashCounterResetDescription": "クラッシュせずに {days} 日経過すると回数をリセットします。上限に達した後は手動でサービスを起動してください。",
    "crashCounterResetTitle": "回数をリセットする期間（日）",
    "crashRecoveryActualTitle": "現在の実際の設定",
    "crashRecoveryUnavailable": "自動復旧の設定を確認できません。",
    "crashRecoveryApplied": "自動復旧の設定を適用しました。",
    "crashRecoverySkipped": "現在の実行方法では自動復旧の設定を適用しません。",
    "crashRecoveryFailed": "自動復旧の設定を適用できませんでした。",
    "crashRecoveryMismatch": "現在の実際の設定が選択した設定と異なります。",
    "crashRecoveryNonCrashWarning": "現在の設定ではエラーを伴う通常停止でも再起動します。",
    "crashRecoveryWriteFailed": "自動復旧の設定を書き込めませんでした。",
    "crashRecoveryReadFailed": "自動復旧の設定を読み取れませんでした。",
    "taskManagerTitle": "タスクマネージャーのショートカット置換",
    "autoStartTitle": "起動時にバックエンドサービスを開始",
    "autoStartDescription": "Windows の起動時にバックエンドサービスを自動的に開始します。",
    "loginStartTitle": "サインイン後にトレイへ表示",
    "loginStartDescription": "このユーザーのサインイン後に画面を起動します。バックエンドサービスは個別に設定します。",
    "loginStartUnavailable": "現在のユーザーのサインイン時起動設定を読み取りまたは保存できませんでした。",
    "loginStartLoading": "サインイン時起動の設定を読み込み中…",
    "loginStartWriteFailed": "サインイン時起動の設定を保存できませんでした。スイッチには読み直した実際の状態が表示されています。",
    "loginStartRetry": "再試行",
    "taskManagerDescription": "Ctrl+Shift+Esc を置き換えます。登録後は、未起動なら Resource Manager を起動し、起動済みならメインウィンドウを表示して前面に出します。Ctrl+Alt+Del は変更しません。",
    "forceTerminateTitle": "強制終了のショートカット",
    "forceTerminateDescription": "前面のアプリと、応答しない表示中のウィンドウを持つすべてのアプリを終了します。初期状態では無効です。",
    "forceTerminateWarning": "このショートカットは即座にプロセスを終了し、未保存の内容は失われます。有効にするには Ctrl、Alt、Win のいずれかと通常のキーの組み合わせが必要です。",
    "hotkeyEmpty": "キーが未設定です",
    "hotkeyAddKey": "キーを追加",
    "hotkeyKeyLabel": "キー",
    "hotkeyRemoveKey": "キーを削除",
    "hotkeyUnordered": "同時に押す",
    "hotkeyOrdered": "前のキーを先に押す",
    "publicServiceTitle": "ローカル共通サービス",
    "publicServiceDescription": "ローカルのソフトウェアに Resource Manager の共通機能の利用を許可します。",
    "publicFileIndexTitle": "ソフトウェアファイルの共通索引",
    "publicFileIndexDescription": "Resource Manager の既存索引を再利用し、問い合わせ時に再スキャンしません。",
    "publicDatabaseServiceTitle": "SQLite データベースサービス",
    "publicDatabaseServiceDescription": "ローカルプログラムに、分離された名前付きデータベース、パラメーター付きクエリ、一括トランザクションを提供します。",
    "publicAiModelCatalogTitle": "AI モデルの統合カタログ",
    "publicAiModelCatalogDescription": "Resource Manager がモデルのカタログとスケジューリングを管理し、LM Studio がモデルを実行します。",
    "lmStudioEndpointTitle": "LM Studio の接続先",
    "lmStudioEndpointDescription": "このコンピューター内のアドレスのみ利用できます。ほかのアプリは Resource Manager 経由で接続します。",
    "lmStudioAutoStartTitle": "必要時に LM Studio を起動",
    "lmStudioAutoStartDescription": "ソフトウェアがモデルを要求し、LM Studio が未起動の場合に起動します。モデル自体は自動で読み込みません。",
    "aiGatewayTitle": "ローカル AI 互換キー",
    "aiGatewayDescription": "LM Studio に直接対応しないソフトウェア向けに、OpenAI または Anthropic 互換キーを生成します。要求はこの PC で動くオープンモデルのみに送信されます。",
    "aiGatewayOpenAiProfile": "OpenAI 互換",
    "aiGatewayAnthropicProfile": "Anthropic 互換",
    "aiGatewayNamePlaceholder": "用途名（任意）",
    "aiGatewayGenerate": "キーを生成",
    "aiGatewayGenerating": "処理中",
    "aiGatewayOneTimeTitle": "キーを生成しました",
    "aiGatewayOneTimeDescription": "キーは一度しか表示されません。再表示できないため、今保存してください。",
    "aiGatewayApiKeyLabel": "API キー",
    "aiGatewayBaseUrlLabel": "ベース URL",
    "aiGatewayCopy": "コピー",
    "aiGatewayRevoke": "キーを取り消す",
    "aiGatewayEmpty": "互換キーはまだ生成されていません。",
    "aiGatewayLoadFailed": "ローカル AI キーを読み込めません",
    "trustedAccess": {
      "title": "信頼済みアクセスキー",
      "description": "汎用アクセスキーと一回限りの認可リクエストを管理します。",
      "loading": "アクセスキーを読み込み中…",
      "loadFailed": "信頼済みアクセスキーを読み込めません。",
      "actionFailed": "信頼済みアクセスの操作に失敗しました。",
      "retry": "再試行",
      "failureReasons": {
        "unauthorized": "ローカルサービスがこの画面のキーを受け付けません。既定キーが無効化された、ローテーションされた直後、または失効している可能性があります。再試行できます。既定キーが無効な場合は、管理者として再度有効にしてください。",
        "scope-required": "現在のキーには「アクセスキーを管理」のスコープがないため、この操作はできません。",
        "default-scope-denied": "既定キーを変更するには、すべての既定スコープを持つキーか、管理者権限が必要です。"
      },
      "defaultTitle": "既定キーのファイル",
      "defaultDescription": "ネイティブシェルが制御されたブリッジを通じて既定キーを読み取り、リクエストに付加します。",
      "defaultEnabled": "有効",
      "defaultDisabled": "無効",
      "defaultKeyName": "既定キー",
      "defaultFilePath": "ファイルの場所",
      "defaultFileLocation": "<インストール フォルダー>\\UserData\\Security\\default-key",
      "defaultSecretLocalOnly": "既定キーを読み取るのはこの PC のネイティブシェルだけです。キーの内容はここに表示されません。",
      "defaultAccessLossWarning": "既定キーを無効にすると直ちに失効し、このキーに依存する画面アクセスが切断されます。ネイティブシェルが再接続できない場合は、独立キーまたは管理者昇格で復旧してください。ローテーションでは古いキーが無効になり、ネイティブシェルが既定キーのファイルを読み直して再接続します。",
      "defaultDisabledRecovery": "既定キーは無効です。この画面は既定キーでローカルサービスにアクセスするため、以降のリクエストは拒否されます。復旧するには、管理者として既定キーを再度有効にしてください。",
      "enableDefaultElevated": "管理者として既定キーを再度有効にする",
      "disableDefault": "既定キーを無効化して失効する",
      "regenerateDefault": "既定キーを再生成",
      "confirmDisableTitle": "既定キーを無効化して失効しますか？",
      "confirmDisableMessage": "この画面は既定キーでローカルサービスにアクセスしています。無効にすると既定キーは直ちに失効し、すべてのページがアクセスできなくなります。復旧するには管理者として既定キーを再度有効にするしかありません。復旧前に画面を閉じると、再度開いた後は画面から復旧できません。",
      "confirmRegenerateTitle": "既定キーを再生成しますか？",
      "confirmRegenerateMessage": "古い既定キーは直ちに無効になります。ネイティブシェルが既定キーのファイルを読み直して再接続するまで、画面のリクエストが拒否されることがあります。",
      "confirmRotateTitle": (name: string) => `「${name}」をローテーションしますか？`,
      "confirmRotateMessage": "古いキーは直ちに無効になり、新しいキーに切り替えるまでそのキーを使うプログラムは拒否されます。新しいキーは一度だけ表示されます。",
      "confirmRevokeTitle": (name: string) => `「${name}」を失効しますか？`,
      "confirmRevokeMessage": "キーは直ちに無効になり、そのキーを使うプログラムは拒否されます。元に戻すことはできません。",
      "createTitle": "独立キーを作成",
      "createDescription": "キーのスコープを選択します。新しいキーは一度だけ表示され、個別にローテーションまたは失効できます。",
      "nameLabel": "名前",
      "namePlaceholder": "例：ローカルスクリプト",
      "createScopesLabel": "付与するスコープ",
      "noScopesAvailable": "現在、新しいキーに付与できるスコープはありません。",
      "create": "独立キーを生成",
      "working": "処理中…",
      "oneTimeTitle": "独立キーを生成しました",
      "oneTimeDescription": "今すぐコピーして保存してください。後から再表示することはできません。",
      "oneTimeAnnouncement": "キーを生成しました。今すぐコピーして保存してください。",
      "apiKeyLabel": "キー",
      "copy": "コピー",
      "copied": "コピーしました",
      "copyFailed": "キーをコピーできません。",
      "dismissSecret": "一回限りのキーを隠す",
      "createdAt": "作成日時",
      "keysTitle": "独立キー",
      "keysEmpty": "独立キーはまだ作成されていません。",
      "revoke": "失効",
      "rotate": "ローテーション",
      "rotateKeyLabel": (name: string) => `「${name}」をローテーション`,
      "revokeKeyLabel": (name: string) => `「${name}」を失効`,
      "requestsTitle": "一回限りの認可リクエスト",
      "requestsDescription": "サードパーティのプログラムがリクエストすると、選択した名前と要求スコープが表示されます。",
      "reviewRequests": "リクエストを確認",
      "requestsLoading": "認可リクエストを待っています…",
      "requestsUnavailable": "認可リクエストの購読を利用できないため、現在は新しいリクエストを受け取れません。",
      "requestsLoadFailed": "保留中の認可リクエストを読み込めません。",
      "requestsStale": "購読の再接続中のため、リクエスト一覧が古い可能性があります。",
      "requestsEmpty": "保留中のリクエストはありません。",
      "requestNamed": (name: string) => `リクエスト名：${name}`,
      "requestCount": (count: number) => `保留中のリクエスト：${count} 件`,
      "expiresAt": "有効期限",
      "observedExecutable": "観測された実行ファイルのパス",
      "observedProcess": "PID",
      "submittedAt": "送信日時",
      "observedCallerIsInformational": "観測されたパスと PID は参考情報であり、認可する相手の識別には使われません。",
      "requestScopesLabel": "要求されたスコープ",
      "requestUnavailableScopeHelp": "現在の許可範囲外のスコープは選択できません。許可時に送信されるのは、リクエストに含まれ、現在許可できるスコープだけです。",
      "approve": "選択したスコープを許可",
      "deny": "拒否",
      "denyReasonLabel": "拒否理由",
      "requestActionFailed": "認可の決定を送信できませんでした。",
      "requestApproved": "許可しました。キーはリクエスト元のプログラムに配信されました。",
      "requestDenied": "拒否しました。",
      "requestDeniedWithReason": (reason: string) => `拒否しました（理由：${reason}）。`,
      "requestExpired": "リクエストの有効期限が切れました。",
      "requestOutcome": (name: string, result: string) => `${name}：${result}`,
      "denialReasons": {
        "not-approved": "許可しない",
        "unsupported": "未対応",
        "duplicate": "重複したリクエスト",
        "not-needed": "不要"
      },
      "scopeLabels": {
        "read": "API の読み取り",
        "metrics.read": "メトリックの読み取り",
        "processes.read": "プロセスの読み取り",
        "session": "セッション操作",
        "adapters.register.self": "このユーザーのアダプターを登録",
        "desktop.publish": "デスクトップ観測情報を公開",
        "gpu.resolve": "GPU 配置を解決",
        "gpu.report": "GPU 起動結果を報告",
        "keys.manage": "アクセスキーを管理",
        "machine.write": "PC 設定を書き込み",
        "injection.configure": "起動時注入を設定"
      },
      "unknownScope": "不明なスコープ（このバージョンでは説明できないため、ここでは付与できません）"
    }
  },
  "debug": {
    "debugModeTitle": "デバッグモード",
    "debugModeDescription": "ローカル診断の項目を表示して有効にします。問題を調べるときだけ有効にしてください。",
    "debugLogTitle": "デバッグログを保存",
    "debugLogDescription": "問題の調査に必要な情報を記録します。ディスク容量と CPU 時間を少量使用します。",
    "hostManagerSmartCoordinatorScoreOnlyTitle": "分析のみ・変更しない",
    "hostManagerSmartCoordinatorScoreOnlyDescription": "監視とスケジューリング結果の生成を続けますが、ソフトウェアやハードウェアの状態は変更しません。",
    "hostManagerSmartCoordinatorPerformanceLogTitle": "スケジューリング性能を記録",
    "hostManagerSmartCoordinatorPerformanceLogDescription": "スケジューリング時間と負荷を記録し、Resource Manager 自身の処理負荷を調べます。"
  },
  "credits": {
    "heroTitle": "Resource Manager を支えるすべての人とプロジェクトに感謝します",
    "heroBody": "このページでは、Resource Manager が利用するオープンソースプロジェクト、実行環境、ハードウェア対応、主な貢献を記録します。",
    "dependencyListTitle": "依存パッケージへの謝辞一覧",
    "dependencyListBody": "以下のすべてのパッケージの作者と保守担当者に感謝します。フロントエンドのロックファイルと解決済み .NET パッケージを、ビルド・テスト・任意依存も含めて記載しています。記載は、この PC で全パッケージが読み込まれたり配布されたりすることを意味しません。",
    "groups": {
      "project": "プロジェクトと貢献",
      "runtime": "ランタイムとビルドツール",
      "windows": "Windows と診断インターフェイス",
      "data": "オフラインデータ",
      "build": "ビルドツール",
      "hardware": "ハードウェア監視と追加サポート"
    },
    "roles": {
      "project": "プロジェクト",
      "frontendFramework": "フロントエンド UI フレームワーク",
      "icons": "画面のアイコン",
      "serialization": "Solid のシリアル化依存",
      "webView": "デスクトップ Web コンテナー",
      "database": "ローカルデータベース",
      "nativeCompiler": "ネイティブコアのコンパイラー",
      "softwareCatalog": "ソフトウェア情報の提供元",
      "buildToolchain": "フロントエンドのビルドツール",
      "typeSystem": "フロントエンドの型システム",
      "buildRuntime": "ビルド用実行環境と型定義",
      "dotnetRuntime": "ローカルアプリの実行環境",
      "hookLibrary": "Windows 互換性サポート",
      "startupInjectionLibrary": "ソフトウェア起動の互換性",
      "traceLibrary": "システム性能の分析",
      "windowsManagement": "Windows のシステム情報",
      "nvidiaTelemetry": "NVIDIA GPU の測定情報",
      "nvidiaExtension": "NVIDIA GPU の拡張インターフェイス",
      "amdTelemetry": "AMD GPU/iGPU の測定情報",
      "signedHardwareAccess": "署名付きハードウェア アクセス",
      "hardwareBridge": "汎用ハードウェアセンサー連携",
      "externalReference": "外部測定の参照・任意補助",
      "deviceIdDatabase": "ハードウェア ID 名称データベース"
    },
    "notes": {
      "windowsFoundation": "OS が提供する Win32、Shell、COM、Direct3D/DXGI、PDH、ETW、サービス、暗号、ネットワーク機能について Microsoft と Windows 開発チームに感謝します。",
      "microsoftTools": "コンパイル、配布、パッケージ管理、自動化を支える .NET SDK、MSBuild、NuGet、PowerShell の作者と保守担当者に感謝します。",
      "managedServices": "サービスのライフサイクル連携と基礎 API を提供する Microsoft と .NET の貢献者に感謝します。このターゲットの TypeExtensions は解決用プレースホルダーで、追加 DLL ではありません。",
      "nativeToolchain": "ネイティブコンパイルと実行基盤を支える GCC と MinGW の作者・保守担当者に感謝します。一部のコンポーネントは libgcc/libstdc++ を静的リンクし、再配布審査は別に行います。",
      "buildContributors": "Solid のコンパイル・更新ツール、ソースマップ、ブラウザーデータ、小型ユーティリティを含む、すべてのビルド依存の作者と保守担当者に感謝します。完全な一覧には、任意プラットフォームも含め、固定された全パッケージとバージョンを残しています。",
      "testContributors": "ブラウザー自動化、回帰テスト、カバレッジを支える Playwright、xUnit.net、Coverlet、Microsoft テスト基盤の貢献者に感謝します。これらは開発ツールで、製品実行時の必須依存ではありません。",
      "upstreamContributors": "ANTLR、Unicode、Mono、Brotli、LLVM の貢献者を含む、.NET と WebView2 の原本通知に記載されたすべての作者に感謝します。原本は全文保持しており、内容は実行中 DLL ごとの一覧ではありません。",
      "resourceManager": "Copyright (c) 2026 BIOcanse。本プロジェクトは Apache-2.0 を使用します。第三者コンポーネントは各自のライセンスを保持します。",
      "solid": "現在の軽量 Web シェルで使用するリアクティブなコンポーネントモデルを提供します。",
      "lucide": "画面には lucide-solid を使用し、Lucide の ISC ライセンスと、Feather 由来アイコンの MIT ライセンス・著作者表示を保持しています。",
      "seroval": "Solid の推移的依存です。MIT ライセンスと Alexis Munsayac の著作者表示を保持しています。",
      "webView2": "デスクトップコンテナーは WebView2 SDK を使用します。システム用 WebView2 Evergreen Runtime は Microsoft が独自の規約で別途配布します。",
      "sqlite": "ローカルデータ保存。Microsoft.Data.Sqlite は MIT、SQLitePCLRaw 3.x は Apache-2.0 です。SourceGear 配布の SQLite コアは上流でパブリックドメインとされています。",
      "zig": "ネイティブコアをコンパイルします。アプリ実行時にコンパイラーは不要です。",
      "softwareCatalog": "オフラインカタログは MIT の WinGet メタデータと CC0 の Wikidata 構造化データを使用します。要約と中国語訳は本プロジェクトで編集しています。",
      "vite": "開発ビルドと Solid フロントエンドのパッケージ化を担当します。",
      "typescript": "画面コードと設定モデルの信頼性を向上させます。",
      "node": "フロントエンドのビルドと開発ツールを支えます。",
      "dotnet": "Resource Manager のローカル実行環境を提供します。",
      "minHook": "フックと命令解析を提供する Tsuda Kageyu、貢献者、HDE 作者 Vyacheslav Patkov に感謝します。原本のライセンスと著作者表示を全文保持しています。",
      "detours": "ソフトウェア起動互換性に使用する Microsoft Research のオープンソースプロジェクトです。原本のライセンスと著作権通知を保持しています。",
      "traceEvent": "Windows のシステム性能情報へのアクセスを提供します。",
      "systemManagement": "Windows のシステムとハードウェアの情報へのアクセスを提供します。",
      "nvml": "NVIDIA GPU の周波数、メモリ、電力、温度の情報を提供します。",
      "nvapi": "NVIDIA GPU のファン、冷却、電気的な情報を追加します。",
      "adlx": "AMD GPU の使用率、周波数、電力、温度、電圧の情報を提供します。",
      "pawnIo": "公式署名済みドライバー。AMD SMU の読み取り、Intel プロセッサ制御、マザーボードとメモリのセンサーで共用します。勝手にはインストールしません。",
      "pawnIoModules": "公式署名済みモジュール（LGPL-2.1）。RyzenSMU と IntelMSR はコンポーネントの確認時に取得し、LPC・EC・SMBus のアクセス モジュールは LibreHardwareMonitorLib に組み込まれています。",
      "libreHardwareMonitor": "同梱の公式コアライブラリー 0.9.6（MPL-2.0）。マザーボードとメモリの 2 グループだけを有効にし、PawnIO 経由でマザーボードの温度・電圧・ファンとメモリ温度を読みます。GUI や Web サーバーは起動しません。",
      "blacktempelToolkits": "LibreHardwareMonitorLib が使うディスク情報、メモリ SPD 読み取り、基盤ライブラリー（1.1.2、1.4.2、1.0.7）。作者 Florian K.、MPL-2.0。",
      "hidSharp": "LibreHardwareMonitorLib が使う USB HID デバイス アクセス ライブラリー 2.6.4。作者 James F. Bellinger、Apache-2.0。",
      "monoPosix": "LibreHardwareMonitorLib が使う Mono POSIX インターフェイス 1.0.0。Microsoft が公開し、Mono クラス ライブラリーの MIT ライセンスに従います。",
      "usbIds": "USB VID/PID のメーカー・デバイス名をオフラインで提供します。デバイス詳細の要求時のみ読み込みます。",
      "pciIds": "PCI VEN/DEV/SUBSYS のメーカー、デバイス、サブシステム名をオフラインで提供します。デバイス詳細の要求時のみ読み込みます。"
    },
    "linkLabels": {
      "official": "公式",
      "github": "GitHub",
      "docs": "ドキュメント",
      "license": "ライセンス",
      "nuget": "NuGet",
      "gpuOpen": "GPUOpen",
      "eula": "EULA",
      "runtime": "実行環境",
      "aspnet": "ASP.NET Core",
      "vite": "Vite",
      "solidPlugin": "Solid プラグイン"
    }
  }
});
