import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("ja-JP", {
  "updates": {
    "autoUpdateTitle": "自動更新",
    "autoUpdateDescription": "自動更新の設定を保存します。信頼できるリリース署名への対応まで自動インストールは行いません。手動更新は利用できます。",
    "installedVersionTitle": "インストール済みバージョン",
    "unknownVersion": "不明",
    "checking": "確認中…",
    "checkForUpdates": "更新を確認",
    "catalogFailed": "リリース一覧を読み込めません：",
    "historyIncomplete": "リリース履歴の読み込みが完了していません。",
    "historyStale": "以前に読み込んだリリース一覧を表示しています。接続を復旧して再試行してください。",
    "versionsTitle": "製品バージョン",
    "versionsDescription": "新しい順に表示します。各系列では初期状態で最新リリースを表示します。",
    "lastChecked": "最終確認：",
    "selectedTarget": "選択したバージョン：",
    "prepare": "選択した更新を準備してインストール",
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
      "无法确认当前安装版本，升级选择已关闭。": "現在のインストール済みバージョンが不明なため、更新の選択は無効です。",
      "只能选择高于当前安装版本的发行版。": "インストール済みバージョンより新しいリリースのみ選択できます。"
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
    "smartMonitoringTitle": "必要時のみ監視",
    "smartMonitoringDescription": (seconds: number) => `データが必要な間だけ監視し、${seconds} 秒間使われなければ停止します。`,
    "adaptiveBooleanModeOptions": [
      {
        "id": "auto",
        "label": "自動",
        "description": "現在の性能モードに合わせて自動選択します。"
      },
      {
        "id": "enabled",
        "label": "常に有効",
        "description": "この設定を常に有効にします。"
      },
      {
        "id": "disabled",
        "label": "常に無効",
        "description": "この設定を常に無効にします。"
      }
    ],
    "gpuPerformanceUseCasesTitle": "GPU の用途",
    "gpuPerformanceUseCasesDescription": "GPU の能力を適切に比較するため、主な用途を選んでください。複数選択できます。",
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
    "automaticMemoryCleanupTitle": "自動解放の閾値",
    "automaticMemoryCleanupDescription": "リソースが少なく、安全な移動を続けられないときにバックグラウンドで解放を開始します。",
    "physicalMemoryAutomaticCleanupLabel": "物理メモリ空き率 %",
    "virtualMemoryAutomaticCleanupLabel": "コミット余裕率 %",
    "memoryOptimizationTargetTitle": "メモリ最適化の目標",
    "memoryOptimizationTargetDescription": "目標を超えた使用量は、第 1 段階の緩やかな解放を継続します。緊急解放は開始しません。",
    "physicalMemoryOptimizationTargetLabel": "物理メモリ目標使用率 %",
    "virtualMemoryOptimizationTargetLabel": "コミット目標使用率 %",
    "pauseHiddenTitle": "非表示時の更新",
    "pauseHiddenDescription": "通常モードでは非表示中の画面更新を一時停止でき、表示時にすぐ再開します。",
    "frontendHiddenRefreshModeOptions": [
      {
        "id": "auto",
        "label": "自動",
        "description": "現在の性能モードに合わせて自動判断します。"
      },
      {
        "id": "pauseWhenHidden",
        "label": "非表示時に停止",
        "description": "ウィンドウが非表示の間、画面更新を停止します。"
      },
      {
        "id": "continueWhenHidden",
        "label": "更新を継続",
        "description": "ウィンドウが非表示の間も画面更新を継続します。"
      }
    ],
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
      "management": {
        "label": "コンポーネントとソフトウェア",
        "description": "コンポーネント状態、ソフトウェア一覧、処理の進捗を更新します。"
      },
      "discovery": {
        "label": "ソフトウェアの移動",
        "description": "移動の進捗と利用可能な内容を更新します。"
      },
      "optimization": {
        "label": "性能の最適化",
        "description": "最適化レポートとスケジューリング状態を更新します。"
      },
      "localSystem": {
        "label": "ローカルシステム",
        "description": "稼働時間など、低頻度で変化するローカル状態を更新します。"
      }
    }
  },
  "appearance": {
    "themeTitle": "配色モード",
    "themeDescription": "システム設定、ライト、ダーク、低コントラストの外観を選びます。",
    "animationTitle": "アニメーション",
    "animationDescription": "通常のアニメーション、アニメーションなし、または負荷を抑えた簡易表示を選びます。",
    "resourceBarHardwareAccelerationTitle": "リソースバーのハードウェアアクセラレーション",
    "resourceBarHardwareAccelerationDescription": "GPU でリソースバーのアニメーションを滑らかにします。表示に問題があれば無効にしてください。",
    "resourceBarHardwareAccelerationModeOptions": [
      {
        "id": "auto",
        "label": "自動",
        "description": "前面では滑らかさを保ち、省電力のバックグラウンドでは負荷を減らします。"
      },
      {
        "id": "enabled",
        "label": "常に有効",
        "description": "リソースバーで常にハードウェアアクセラレーションを使います。"
      },
      {
        "id": "disabled",
        "label": "常に無効",
        "description": "リソースバーで常に互換描画を使います。"
      }
    ],
    "barColorTitle": "バーの色",
    "barColorDescription": "ソフトウェア種別ごとの固定色、またはソフトウェアごとの個別色を選びます。",
    "byteUnitTitle": "容量の単位",
    "byteUnitDescription": "1 GiB = 1024 MiB、1 GB = 1000 MB。切り替えると単位名だけでなく数値も換算されます。",
    "fontSmoothingTitle": "文字のアンチエイリアス",
    "fontSmoothingDescription": "文字の鮮明さと描画負荷を自動調整するか、描画方式を固定します。",
    "languageTitle": "表示言語",
    "languageDescription": "Resource Manager の画面で使用する言語を選択します。「システム」は Windows の表示言語を使用します。",
    "settingsLanguageTitle": "表示言語",
    "settingsLanguageDescription": "トレイとウィンドウタイトルを含む画面全体の言語を変更します。システム設定では Windows の表示言語を使用します。",
    "languageSelectLabel": "言語",
    "themeOptions": {
      "system": "システム",
      "light": "ライト",
      "dark": "ダーク",
      "lowContrast": "低コントラスト"
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
    "gpuPerformanceUseCaseOptions": {
      "general": {
        "label": "一般",
        "description": "ラスタライズ性能の基礎点に小さな世代加点を常に追加します"
      },
      "ai": {
        "label": "AI",
        "description": "世代の重みを増やし、NVIDIA のエコシステム、VRAM 容量と帯域幅を重視します"
      },
      "gaming": {
        "label": "ゲーム",
        "description": "最大の世代加点と、小さな VRAM 容量加点を適用します"
      }
    }
  },
  "systemIntegration": {
    "taskManagerTitle": "タスクマネージャーのショートカット置換",
    "autoStartTitle": "Windows とともに起動",
    "autoStartDescription": "起動時にサービスを実行し、サインイン後はトレイに常駐します。",
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
    "aiGatewayLoadFailed": "ローカル AI キーを読み込めません"
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
      "etwToolkit": "Windows 性能分析ツール",
      "nvidiaTelemetry": "NVIDIA GPU の測定情報",
      "nvidiaExtension": "NVIDIA GPU の拡張インターフェイス",
      "amdTelemetry": "AMD GPU/iGPU の測定情報",
      "amdCpuSdk": "AMD CPU センサー SDK",
      "intelTelemetry": "Intel CPU / MSR の測定情報",
      "amdSmuBoundary": "AMD SMU のアクセス境界",
      "hardwareBridge": "汎用ハードウェアセンサー連携",
      "notebookEc": "ノートパソコンのファン対応",
      "externalReference": "外部測定の参照・任意補助",
      "latencyDiagnostics": "遅延診断の補助",
      "deviceIdDatabase": "ハードウェア ID 名称データベース"
    },
    "notes": {
      "windowsFoundation": "OS が提供する Win32、Shell、COM、Direct3D/DXGI、PDH、ETW、サービス、暗号、ネットワーク機能について Microsoft と Windows 開発チームに感謝します。",
      "microsoftTools": "コンパイル、配布、パッケージ管理、自動化を支える .NET SDK、MSBuild、NuGet、PowerShell の作者と保守担当者に感謝します。",
      "managedServices": "サービスのライフサイクル連携と基礎 API を提供する Microsoft と .NET の貢献者に感謝します。このターゲットの TypeExtensions は解決用プレースホルダーで、追加 DLL ではありません。",
      "nativeToolchain": "ネイティブコンパイルと実行基盤を支える GCC と MinGW の作者・保守担当者に感謝します。一部のコンポーネントは libgcc/libstdc++ を静的リンクし、再配布審査は別に行います。",
      "buildContributors": "Solid のコンパイル・更新ツール、ソースマップ、ブラウザーデータ、小型ユーティリティを含む、すべてのビルド依存の作者と保守担当者に感謝します。完全な一覧には、任意プラットフォームも含め、固定された全パッケージとバージョンを残しています。",
      "testContributors": "ブラウザー自動化、回帰テスト、カバレッジを支える Playwright、xUnit.net、Coverlet、Microsoft テスト基盤の貢献者に感謝します。これらは開発ツールで、製品実行時の必須依存ではありません。",
      "openHardwareMonitor": "センサーインターフェイスを提供する OpenHardwareMonitor の作者・貢献者に感謝します。インストール済みの外部プロバイダーから WMI データを取得できますが、同梱を意味しません。",
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
      "wpt": "Windows 性能調査用の Microsoft 診断ツールです。別途インストールするもので、基本アプリには付属しません。",
      "nvml": "NVIDIA GPU の周波数、メモリ、電力、温度の情報を提供します。",
      "nvapi": "NVIDIA GPU のファン、冷却、電気的な情報を追加します。",
      "adlx": "AMD GPU の使用率、周波数、電力、温度、電圧の情報を提供します。",
      "ryzenMaster": "AMD プロセッサーの電力、電圧、電流、温度の情報を追加します。インストールとライセンスには明示的な確認が必要です。",
      "intelPcm": "Intel プロセッサーの電力、周波数、温度の情報を追加します。",
      "pawnIo": "AMD SMU PM 表を読み取る公式署名ドライバーの経路です。読み取り専用で、無断インストールはしません。",
      "libreHardwareMonitor": "任意の CPU・システムファン、メモリ温度、マザーボード温度、VRM 温度、チップセット温度、電圧の監視項目を提供します。",
      "notebookFanControl": "汎用センサーや GPU ドライバーでノートパソコンのファン値を取得できない場合の任意経路です。",
      "afterburner": "外部の比較・参照と任意の補助に使用します。実行時の必須依存ではありません。",
      "latencyMon": "比較ツールとして ISR、DPC、ハードページフォールト、ドライバー遅延の診断を補助します。",
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
