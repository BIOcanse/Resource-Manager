import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../index.ts";

export const deviceActions: AppCopy["deviceActions"] = {
  restart: {
    label: "デバイスを再起動",
    busy: "再起動中",
    hint: "このレポートが指すデバイスを無効にしてから再び有効にします。止まったドライバーが動き出すことがあります。毎回あなたの確認が必要で、ソフトウェアが自動で実行することはありません。",
    confirmTitle: "このデバイスを再起動しますか？",
    confirmMessage: (target: string) => cjkText`「${target}」に対応するデバイスを無効にしてから再び有効にします。`,
    confirmDetails: [
      "確認後に一度だけ実行し、自動で繰り返すことはありません。",
      "管理者権限が必要です。結果は詳細のデバイス操作記録に書き込まれます。"
    ],
    risks: {
      DeviceRestartNetworkInterrupted: "ネットワークアダプターの場合、ネットワーク接続が一時的に切れます。",
      DeviceRestartDisplayStateLost: "グラフィックスアダプターの場合、画面が一瞬暗くなり、ウィンドウの位置や表示設定が失われることがあります。",
      DeviceRestartMayRequireReboot: "一部のデバイスはコンピューターを再起動しないと回復しません。"
    },
    unknownRisk: (code: string) => cjkText`その他のリスク（${code}）。`,
    confirm: "デバイスを再起動",
    doneTitle: "デバイスを再起動しました",
    refusedTitle: "デバイスは再起動されませんでした",
    failed: "デバイスを再起動できませんでした。",
    reason: (text: string) => cjkText`結果：${text}`,
    deviceResult: (text: string) => cjkText`デバイスの結果：${text}`,
    win32Detail: (code: string) => cjkText`システムエラーコード：${code}`,
    recorded: (sequence: string) => cjkText`デバイス操作記録に書き込みました（番号 ${sequence}）。`,
    notRecorded: "この要求は記録されませんでした。"
  },
  reasons: {
    completed: "完了",
    "confirmation-required": "確認が必要です",
    "report-not-found": "レポート項目が見つかりません",
    "unsupported-report": "この種類のレポートはデバイスの再起動に対応していません",
    "device-evidence-unavailable": "レポートにデバイスを特定できる情報がありません",
    "invalid-settings": "設定が無効です",
    "record-capacity-reached": "デバイス操作記録がいっぱいのため、実行しませんでした",
    "persistence-failed": "記録を書き込めなかったため、実行しませんでした",
    "device-resolution-failed": "再起動するデバイスを特定できませんでした",
    "device-restart-failed": "デバイスの再起動に失敗しました",
    "notification-failed": "デバイスは処理しましたが、通知を送れませんでした"
  },
  results: {
    success: "成功",
    "confirmation-required": "確認が必要です",
    "invalid-instance-id": "デバイスインスタンス ID が無効です",
    "administrator-required": "管理者権限が必要です",
    "device-not-found": "デバイスが見つかりません",
    "device-unavailable": "デバイスは現在使用できません",
    "disable-failed": "デバイスの無効化に失敗しました",
    "enable-failed": "デバイスの再有効化に失敗しました",
    "reboot-required": "コンピューターの再起動が必要です",
    "verification-failed": "再起動後の確認に合格しませんでした",
    "driver-not-found": "ドライバーが見つかりません",
    "ambiguous-device": "複数のデバイスが該当したため、実行しませんでした",
    "invalid-module": "ドライバーモジュールが無効です",
    "enumeration-failed": "デバイスの列挙に失敗しました",
    "buffer-too-small": "システムバッファーが不足しています",
    "unsupported-platform": "このシステムでは対応していません",
    interrupted: "実行中に中断されたため、結果は不明です"
  },
  unknownCode: (code: string) => cjkText`コード ${code}`,
  records: {
    tab: "デバイス操作記録",
    intro: "レポートで「デバイスを再起動」を確認するたびに、その結果をここに記録します。記録は保存され続け、自動で削除されることはありません。実行中の記録は操作が終わると履歴に移ります。",
    loading: "デバイス操作記録を読み込んでいます…",
    readFailed: "デバイス操作記録を読み込めませんでした",
    empty: "デバイス操作記録はまだありません。",
    loadMore: "以前の記録を読み込む",
    loadingMore: "読み込み中",
    refresh: "更新",
    shown: (count: string) => cjkText`${count}件の記録を表示しています。`,
    states: { active: "実行中", history: "履歴" },
    device: "デバイスインスタンス",
    report: "レポート項目",
    time: "時刻",
    result: "結果",
    detail: "システムエラーコード",
    sequence: "番号"
  }
};

export const crashDumpKeys: AppCopy["crashDumpKeys"] = {
  title: "クラッシュダンプのキー",
  description: "オンにすると、右 Ctrl キーを押したまま Scroll Lock キーを 2 回押したときに、システムがブルースクリーンで停止してメモリダンプを書き込みます。コンピューターが固まって普通に操作できないとき、後で分析できるように状態を残せます。既定ではオフです。",
  details: [
    "このキーを押したときだけ働き、それ以外は何もしません。",
    "実行するとコンピューターはすぐに再起動し、保存していない作業は失われます。",
    "変更はコンピューターの再起動後に有効になり、書き込みには管理者権限が必要です。オフにすると、オンにする前のシステム設定に戻します。"
  ],
  notProvided: "バックエンドがこの設定を提供していません。",
  statusTitle: "システム内の状態",
  states: {
    takesEffectAfterRestart: "システムに書き込みました。コンピューターの再起動後に有効になります。",
    off: "オンになっていません。",
    restorePending: "オンにする前のシステム設定に戻しています。コンピューターの再起動後に完了します。",
    configurationMismatch: "システム内のキー設定がここでの選択と一致しません。",
    failed: "システム設定を書き込めませんでした。"
  },
  combinations: {
    rightCtrlScrollLockTwice: "キー：右 Ctrl + Scroll Lock を 2 回",
    custom: "キー：他のプログラムまたは手動で設定した独自のキー"
  },
  kernelDumpMissing: "システムがカーネルメモリダンプを書き込むように設定されていないため、実行してもダンプが書き込まれないことがあります。",
  reason: (text: string) => cjkText`理由：${text}`,
  win32Detail: (code: string) => cjkText`システムエラーコード：${code}`,
  reasons: {
    "invalid-crash-dump-keys": "キー設定が無効です",
    "crash-dump-keys-read-failed": "システム設定を読み込めませんでした",
    "crash-dump-keys-write-failed": "システム設定を書き込めませんでした",
    "crash-dump-keys-verify-failed": "書き込み後の確認に合格しませんでした",
    "crash-dump-keys-restore-pending": "オンにする前のシステム設定がまだ戻りきっていません",
    "invalid-crash-dump-key-snapshot": "保存した元の設定が無効です",
    "crash-dump-keys-access-denied": "管理者権限が必要です",
    "crash-dump-key-value-too-large": "システム内の元の値が大きすぎて保存できません",
    "crash-dump-keys-configuration-mismatch": "システム内の設定が他のプログラムに変更されました",
    "crash-dump-keys-bridge-failed": "ローカルサービスがこの設定を処理できませんでした"
  }
};

export const cpuMonopoly: AppCopy["cpuMonopoly"] = {
  intro: "優先度が高またはリアルタイムのプロセスが CPU を占有し続けると、システムが固まって応答しなくなることがあります。オンにすると、そのようなプロセスを通常の優先度に戻します。変えるのは優先度だけで、プロセスの終了や一時停止はせず、優先度を元に戻すこともしません。ホストメモリ保護のプロセス除外リストも適用されます。既定ではオフです。",
  notProvided: "バックエンドが CPU 占有保護の設定を提供していません。",
  unsavedNote: "保存していない変更があります。保存すると有効になります。",
  rejected: (reason: string) => cjkText`設定は受け入れられませんでした：${reason}`,
  enabledTitle: "CPU 占有保護を有効にする",
  enabledDescription: "オンにすると、各プロセスの CPU 使用率と優先度をバックグラウンドで確認します。オフのときは確認しません。",
  thresholdTitle: "使用率のしきい値",
  thresholdDescription: "優先度が高またはリアルタイムのプロセスの CPU 使用率（全プロセッサに対する割合）がこの値に達すると計測を始めます。",
  thresholdLabel: "パーセント",
  durationTitle: "継続時間",
  durationDescription: "使用率がしきい値に達した状態がこの時間続くと、通常の優先度に戻します。",
  durationLabel: "秒",
  intervalTitle: "確認間隔",
  intervalDescription: "各プロセスの CPU 使用率を読み取る間隔です。",
  intervalLabel: "秒",
  whitelistNote: "除外リストは「ホストメモリ保護」セクションで編集します。",
  statusTitle: "現在の状態",
  statusLoading: "CPU 占有保護の状態を読み込んでいます…",
  statusUnavailable: "CPU 占有保護の状態を読み込めませんでした",
  statusUnavailableDetail: "設定は引き続き編集、保存できます。状態は取得でき次第ここに表示されます。",
  stateActive: "確認中",
  stateInactive: (reason: string) => cjkText`確認していません：${reason}`,
  inactiveReasons: {
    disabled: "オフ",
    "not-running": "保護はまだ開始していません",
    "process-data-unavailable": "プロセスのデータを読み取れません",
    "process-exit-proof-unavailable": "プロセスが終了したかどうかを確認できません",
    "process-capacity": "追跡できる上限を超えるプロセスがあります",
    stopped: "停止しました"
  },
  currentAction: "有効な処理",
  exclusionsTitle: "除外されたプロセス",
  exclusionsEmpty: "現在除外されているプロセスはありません。",
  exclusionReasons: {
    "resource-manager": "このソフトウェア自身のプロセス",
    "resource-manager-process": "このソフトウェア自身のプロセス",
    "protected-process": "除外リストに含まれています",
    "critical-process": "システムの重要なプロセス",
    "unsupported-platform": "このシステムでは対応していません",
    "identity-changed": "プロセスが変わりました",
    "cannot-open": "このプロセスにアクセスできません",
    "criticality-unavailable": "システムの重要なプロセスかどうかを確認できません"
  },
  process: (name: string, processId: number) => cjkText`${name}（PID ${processId}）`,
  percent: (value: string) => `${value}%`,
  seconds: (value: string) => cjkText`${value} 秒`,
  priorities: { High: "高", RealTime: "リアルタイム" },
  records: {
    tab: "CPU 占有保護の記録",
    intro: "CPU 占有保護は処理したプロセスごとに 1 件記録します。通常の優先度に戻した後、プロセスが終了するか、あなたがその優先度を設定し直すと、記録は履歴に移ります。失敗した処理は最初から履歴です。記録は保存され続け、自動で削除されることはありません。",
    loading: "CPU 占有保護の記録を読み込んでいます…",
    unavailable: "CPU 占有保護の記録を読み込めませんでした",
    empty: "処理の記録はまだありません。",
    count: (count: string, capacity: string) => cjkText`記録は${count}件、最大${capacity}件まで保存します。`,
    unrecorded: (count: number) => cjkText`記録がいっぱいのため、その後の${count}回の処理は記録されていません。履歴を消去すると記録を再開します。`,
    outcomes: { normalized: "通常の優先度に戻しました", failed: "処理に失敗しました" },
    states: { active: "有効中", history: "履歴" },
    time: "時刻",
    cpu: "CPU 使用率",
    previousPriority: "元の優先度",
    threshold: "当時のしきい値",
    duration: "当時の継続時間",
    reason: "理由",
    detail: "詳細",
    endedAt: "終了時刻",
    endReason: "終了理由"
  },
  actionReasons: {
    "process-exited": "プロセスが終了しました",
    "user-priority-set": "優先度が別に設定されました",
    "write-failed": "優先度を書き込めませんでした",
    "identity-changed": "プロセスが変わりました",
    "priority-changed": "優先度が他のプログラムに変更されました",
    "priority-unavailable": "プロセスの優先度を読み取れません",
    "priority-write-failed": "優先度を書き込めませんでした",
    "priority-readback-unavailable": "書き込み後に優先度を読み戻せませんでした",
    "priority-readback-mismatch": "書き込み後に読み戻した優先度が一致しません"
  },
  notice: {
    title: "CPU 占有保護",
    normalized: (target: string) => cjkText`${target}を通常の優先度に戻しました。`,
    failed: (target: string, reason: string) => cjkText`${target}を調整できませんでした：${reason}`,
    openRecords: "記録を表示"
  }
};
