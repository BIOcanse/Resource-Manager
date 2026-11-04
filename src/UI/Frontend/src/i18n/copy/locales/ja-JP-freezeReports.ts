import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../index.ts";

export const freezeReports: AppCopy["freezeReports"] = {
  systemEvents: {
    tab: "システム イベント",
    intro: "ローカル サービスは、システム ログからストレージの再試行とリセット、グラフィックス ドライバーのリセット、メモリ不足、デスクトップ プロセスのクラッシュ、ハードウェア エラーなどのイベントを収集し、カテゴリと対象ごとに項目としてまとめます。これらの問題は本ソフトウェアでは解決できないため、ここでは検出と報告のみを行います。長期間新しいイベントがない項目は履歴に移動します。",
    loading: "システム イベント レポートを読み込んでいます…",
    readFailed: "システム イベント レポートを読み込めませんでした。",
    notObservedTitle: "システム イベントを記録していません",
    notObservedDetail: "システム イベントの記録は実行されていません。設定で無効にされたか、まだ開始されていない可能性があります。実行されるまで項目は作成されません。",
    unavailableTitle: "システム イベントの記録に失敗しました",
    unavailableDetail: "ローカル サービスはシステム ログを読み取れません。原因は上に表示されています。",
    emptyTitle: "システム イベントはありません",
    emptyDetail: "システム ログに報告が必要なイベントは見つかっていません。",
    status: {
      label: "記録の状態",
      observing: "記録中",
      "not-observed": "記録していません",
      unavailable: "記録に失敗しました"
    },
    activeCount: "アクティブな項目",
    historyCount: "履歴の項目",
    machineTarget: "コンピューター全体",
    count: "回数",
    times: (count: number) => cjkText`${count}回`,
    firstObserved: "初回発生",
    lastObserved: "最終発生",
    overview: "概要",
    categoryLabel: "カテゴリ",
    target: "対象",
    state: "状態",
    activeEntry: "アクティブ",
    historyEntry: "履歴",
    category: {
      storage_retry_reset: "ストレージの再試行とリセット",
      gpu_reset: "グラフィックス ドライバーのリセット",
      memory_exhausted: "メモリ不足",
      desktop_crash: "デスクトップ プロセスのクラッシュ",
      hardware_error: "ハードウェア エラー",
      unexpected_shutdown: "予期しないシャットダウンとブルー スクリーン"
    },
    targetKindLabel: "対象の種類",
    targetKind: {
      machine: "コンピューター全体",
      physical_disk: "物理ディスク",
      storage_port: "ストレージ コントローラーのポート",
      volume: "ボリューム",
      gpu_device: "グラフィックス デバイス",
      desktop_process: "デスクトップ プロセス",
      hardware: "ハードウェア"
    },
    time: "日時",
    channel: "ログ",
    channelName: {
      system: "システム",
      application: "アプリケーション"
    },
    provider: "ソース",
    eventId: "イベント ID",
    recordId: "レコード ID",
    bugcheckCode: "停止コード",
    dumpPath: "ダンプ ファイル",
    failure: (name: string) => cjkText`システム ログを読み取れませんでした（${name}）。既存の項目は保持され、読み取りが回復すると記録を再開します。`,
    liveKernelFailure: (name: string) => cjkText`LiveKernelReports フォルダーをスキャンできませんでした（${name}）。システム ログのイベントには影響しません。`,
    unrecorded: (count: number) => cjkText`項目数の上限に達したため、さらに${count}個の項目が記録されていません。`,
    items: "システム イベントの項目",
    detailsButton: "詳細",
    back: "一覧に戻る",
    eventsTitle: (count: number) => cjkText`最近の元のイベント（${count}件）`,
    xml: "イベント XML"
  },
  recorder: {
    tab: "フリーズ レコーダー",
    intro: "ローカル サービスは、メモリ、カーネル プール、ディスク、割り込みの少量のデータを固定サイズの記録ファイルに継続して書き込みます。コンピューターがフリーズして強制的に電源を切られた場合やブルー スクリーンで停止した場合は、次回の起動時に記録とシステム ログを照合して再起動後レビューを作成します。フリーズ前の記録、同じ期間のシステム イベント、判定結果が含まれます。これらの情報はここで検出と報告のみを行います。",
    loading: "フリーズ レコーダーを読み込んでいます…",
    readFailed: "フリーズ レコーダーを読み込めませんでした。",
    emptyTitle: "再起動後レビューはありません",
    emptyDetail: "前回の起動以降、予期しないシャットダウンやブルー スクリーンは検出されていません。",
    recorder: "レコーダー",
    running: "記録中",
    stopped: "記録していません",
    lastRecordAt: "最終記録",
    reviewCount: "再起動後レビュー",
    failure: (name: string) => cjkText`レコーダーが失敗しました（${name}）。記録を書き込んでいません。`,
    stoppedNote: "レコーダーは実行されていません。設定で無効にされた可能性があります。この間に発生したフリーズは完全にはレビューできません。",
    gapsTitle: "現在の記録で不足しているデータ",
    gap: {
      "no-sample": "まだ記録が書き込まれていません",
      "interrupt-cpu-share": "割り込みが占めるプロセッサ時間",
      "longest-interrupt": "最長の単一割り込み",
      "active-kernel-leak-system-item": "システム全体のカーネル リーク項目がアクティブかどうか",
      "process-private-commit": "プロセスのプライベート コミット",
      "physical-disk-inventory": "物理ディスクの一覧",
      "physical-disk-completed-operations": "物理ディスクの完了操作数",
      commit: "コミットとコミット上限",
      "physical-memory": "物理メモリ",
      "kernel-pools": "ページ プールと非ページ プール"
    },
    reviews: "再起動後レビュー",
    reviewTitle: (time: string) => cjkText`予期しないシャットダウン · ${time}`,
    shutdownAt: "シャットダウンまたはブルー スクリーンの日時",
    gapDuration: "間隔",
    bugcheckCode: "停止コード",
    tagsTitle: "判定結果",
    noTags: "判定結果はありません。",
    inputsIncomplete: "レビュー時に一部の記録またはシステム ログを完全に読み取れなかったため、結果が不完全な可能性があります。",
    tag: {
      bugcheck: {
        name: "ブルー スクリーン",
        description: "システム ログにブルー スクリーンが記録されています。停止コードは概要に表示されます。"
      },
      "commit-exhausted": {
        name: "コミット不足",
        description: "記録されたコミットがメモリ保護の即時ラインに達したか、同じ期間にシステムがメモリ不足を報告しました。"
      },
      "physical-exhausted": {
        name: "物理メモリ不足",
        description: "記録された使用可能な物理メモリが、設定で指定した割合を下回りました。"
      },
      "pool-high": {
        name: "カーネル プール過大",
        description: "非ページ プールが物理メモリに対して指定した割合を超えたか、システム全体のカーネル リーク項目がアクティブでした。"
      },
      "storage-unresponsive": {
        name: "ストレージ応答なし",
        description: "同じ期間にストレージの再試行またはリセットが発生したか、ディスクが応答なしの条件を連続して満たしました。"
      },
      "gpu-reset": {
        name: "グラフィックス ドライバーのリセット",
        description: "同じ期間にグラフィックス ドライバーがリセットされました。"
      },
      "hardware-error": {
        name: "ハードウェア エラー",
        description: "同じ期間にシステムがハードウェア エラーを報告しました。"
      },
      "recording-stopped-normal": {
        name: "正常な状態で記録が停止",
        description: "上記のいずれにも該当せず、最後の記録はすべて正常でした。考えられる原因は、ストレージ ドライバーやフィルター ドライバーの停止、突然の電源断、電源ボタンの長押しです。"
      }
    },
    details: {
      button: "詳細",
      back: "一覧に戻る",
      loading: "レビューを読み込んでいます…",
      readFailed: "レビューを読み込めませんでした。",
      export: "エクスポート",
      exporting: "エクスポート中",
      exportFailed: "レビューをエクスポートできませんでした。",
      overview: "概要",
      reviewWindow: "レビュー期間",
      timeRange: (from: string, to: string) => cjkText`${from} ～ ${to}`,
      samplesSeen: "期間内の記録数",
      inputsComplete: "データの完全性",
      yes: "はい",
      no: "いいえ",
      dumpPath: "ダンプ ファイル",
      thresholdsTitle: "レビューに使用したしきい値",
      commitImmediatePercent: "コミットの即時ライン",
      physicalAvailablePercent: "使用可能な物理メモリの下限",
      nonpagedPoolPercent: "非ページ プールの上限（物理メモリに対する割合）",
      storageConsecutive: "ディスク応答なしの連続記録数",
      sampleInterval: "記録間隔",
      reviewWindowLength: "レビュー期間の長さ",
      curvesTitle: "記録の推移",
      curve: {
        commit: "コミット（上限に対する割合）",
        physicalAvailable: "使用可能な物理メモリ（合計に対する割合）",
        nonpagedPool: "非ページ プール",
        pagedPool: "ページ プール",
        diskQueue: "ディスク キューの長さ（最大）",
        diskLatency: "ディスクの待ち時間（最大）",
        interrupt: "割り込みが占めるプロセッサ時間"
      },
      curveRange: (min: string, max: string) => cjkText`${min} ～ ${max}`,
      noCurveData: "データなし",
      noRecords: "レビュー期間内に記録はありません。",
      lastRecordTitle: "最後の記録",
      time: "日時",
      commit: "コミット",
      physicalAvailable: "使用可能な物理メモリ",
      ofLimit: (formattedValue: string, formattedLimit: string) => cjkText`${formattedValue} / ${formattedLimit}`,
      longestInterrupt: "最長の単一割り込み",
      kernelLeakActive: "システム全体のカーネル リーク項目がアクティブ",
      processesTitle: "プライベート コミットが多いプロセス",
      processName: (name: string, pid: string) => cjkText`${name}（PID ${pid}）`,
      diskName: (id: string) => cjkText`物理ディスク ${id}`,
      queueDepth: "キューの長さ",
      latency: "待ち時間",
      completedOperations: "完了操作数",
      eventsTitle: (count: number) => cjkText`期間内のシステム イベント（${count}件）`,
      noEvents: "レビュー期間内にシステム イベントは受信されませんでした。",
      actualShutdownAt: "実際のシャットダウン日時"
    }
  },
  settings: {
    open: "設定",
    back: "レポートに戻る",
    systemEventsTitle: "システム イベントの設定",
    recorderTitle: "フリーズ レコーダーの設定",
    readFailed: "設定を読み込めませんでした。",
    save: "保存",
    saving: "保存中",
    discard: "変更を破棄",
    saved: "設定を保存しました。",
    saveFailed: "設定を保存できませんでした。",
    invalid: "設定が検証に合格しなかったため保存されませんでした。値を確認してください。",
    conflict: "設定は保存されませんでした。ルール表、記録数、記録ファイルの場所は実行中に変更できません。先に該当する機能を無効にして保存してから、これらの項目を変更してください。記録ファイルが既に存在する場合、記録数を変更するには新しい場所も指定する必要があります。",
    resourceBound: "設定は保存されませんでした。必要なリソースが上限を超えています。容量または記録数を減らしてください。",
    draftInvalid: "現在の変更は不完全なため（ルールにソース名がないなど）保存できません。",
    rulesLocked: "システム イベントの記録が有効な間はルール表を変更できません。先に無効にして保存してください。",
    recorderLocked: "レコーダーが有効な間はこの項目を変更できません。先に無効にして保存してください。",
    units: {
      minutes: "分",
      seconds: "秒",
      milliseconds: "ミリ秒",
      percent: "パーセント",
      count: "個"
    },
    systemEvents: {
      enabled: {
        title: "システム イベントを記録する",
        description: "システム ログからイベントを受信し、項目としてまとめます。無効にすると新しいイベントは受信されず、既存の項目は保持されます。"
      },
      historyAfter: {
        title: "履歴に移動するまでの時間",
        description: "最後のイベントの後、この時間新しいイベントがない項目は履歴に移動します。"
      },
      eventTailCapacity: {
        title: "項目ごとに保持する元のイベント数",
        description: "詳細ページ用に各項目が保持する最近の元のイベントの数です。"
      },
      entryCapacity: {
        title: "項目数の上限",
        description: "保持する項目の最大数です。上限に達すると、新しい項目は数えるだけで記録されません。"
      },
      liveKernelRoot: {
        title: "LiveKernelReports の場所",
        description: "システムがライブ カーネル ダンプ レポートを保存するフォルダーです。グラフィックス ドライバーのタイムアウトなどのレポートを見つけるために使用します。"
      },
      liveKernelDirectories: {
        title: "スキャンするサブフォルダー",
        description: "上記の場所の下でスキャンするサブフォルダーです。1 行に 1 つ指定します。"
      },
      liveKernelEntryCapacity: {
        title: "ダンプ レポートの上限",
        description: "各スキャンで記録するダンプ レポートの最大数です。"
      },
      rules: {
        title: "イベント ルール",
        description: "システム ログのどのイベントがどのカテゴリに属し、各イベントがどの種類の対象を指すかを定めます。"
      }
    },
    recorder: {
      enabled: {
        title: "フリーズ データを記録する",
        description: "記録を継続して書き込み、予期しないシャットダウンの後の次回起動時にレビューを作成します。無効にすると記録は停止し、記録ファイルは保持されます。"
      },
      sampleInterval: {
        title: "記録間隔",
        description: "2 つの記録の間の時間です。短いほど、最後の記録がフリーズの時点に近くなります。"
      },
      reviewWindow: {
        title: "レビュー期間",
        description: "レビューで最後の記録からどれだけ前までの記録とシステム イベントを表示するかを指定します。"
      },
      commitImmediate: {
        title: "コミットの即時ライン",
        description: "コミット不足の判定に使用するラインです。メモリ保護の即時ラインに従い、メモリ保護の設定で変更します。"
      },
      commitImmediateNone: "未設定",
      physicalAvailable: {
        title: "使用可能な物理メモリの下限",
        description: "使用可能な物理メモリが合計に対してこの割合を下回ると、物理メモリ不足と判定します。"
      },
      nonpagedPool: {
        title: "非ページ プールの上限",
        description: "非ページ プールが物理メモリに対してこの割合を超えると、カーネル プール過大と判定します。"
      },
      storageConsecutive: {
        title: "ディスク応答なしの連続記録数",
        description: "ディスクがこの数の記録で連続して条件を満たすと、ストレージ応答なしと判定します。"
      },
      slots: {
        title: "記録数",
        description: "記録ファイルに保持できる記録の数で、レビューでさかのぼれる範囲を決めます。記録ファイルが既に存在する場合、変更するには新しい場所も指定する必要があります。"
      },
      ringPath: {
        title: "記録ファイルの場所",
        description: "記録ファイルの完全なパスです。空のままにすると既定の場所を使用します。"
      },
      ringPathDefault: "既定の場所"
    },
    ruleTable: {
      channel: "ログ",
      provider: "ソース名",
      ids: "イベント ID",
      idsHint: "コンマで区切ります",
      category: "カテゴリ",
      targetKind: "対象の種類",
      remove: "削除",
      add: "ルールを追加"
    }
  }
};
