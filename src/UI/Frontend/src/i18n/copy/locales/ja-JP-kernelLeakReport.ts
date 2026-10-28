import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "カーネル リーク レポート",
  intro: "ローカル サービスは実行中、各プロセスが保持するカーネル リソース（現在はプロセス ハンドル）を継続して観察し、増え続けて際限なく増加している疑いのあるプロセスと、その判定根拠を一覧表示します。判定結果は疑いを示すだけです。対処するかどうかは判定根拠に基づいて決定してください。",
  loading: "カーネル リーク レポートを読み込んでいます…",
  readFailed: "カーネル リーク レポートを読み込めませんでした",
  capturedAt: "取得日時",
  suspectedItems: "疑いのある項目",
  historyItems: "履歴の項目",
  unrecordedItems: "上限のため未記録",
  items: "レポート項目",
  historyEntry: "履歴",
  notObservedTitle: "カーネル リークを観察していません",
  notObservedDetail: "カーネル リークの観察は現在実行されていません。オフになっている、観察計画がまだ公開されていない、または停止した可能性があります。実行されるまでサンプリングもレポートの作成も行いません。",
  observingTitle: "観察中",
  observingDetail: "今回の観察の時間とサンプル数がどちらも要件を満たすと判定を開始します。",
  emptyTitle: "カーネル リークの疑いは見つかりませんでした",
  emptyDetail: "今回の観察の時間とサンプル数は要件を満たしており、増え続けているカーネル リソースは見つかりませんでした。",
  unavailableTitle: "今回の観察は失敗しました",
  failure: {
    SourceUnavailable: "プロセスのリソース データを読み取れませんでした。",
    CapacityExceeded: "観察対象のプロセス数が容量の上限を超えたため、今回の観察を停止しました。",
    InvalidObservation: "サンプル データが無効だったため、今回の観察を停止しました。"
  },
  observation: {
    title: "今回の観察",
    startedAt: "開始日時",
    elapsed: "観察時間",
    samples: "サンプル数",
    notStarted: "まだサンプリングしていません",
    reachedOfRequired: (reached: string, required: string) => `${reached}（必要: ${required}）`
  },
  previousRun: {
    title: "前回の観察は失敗しました",
    reason: "理由",
    startedAt: "開始日時",
    failedAt: "失敗日時"
  },
  restartPolicy: {
    NextInterval: "ローカル サービスは次のサンプリング間隔で新しい観察を自動的に開始します。"
  },
  unavailableHolders: (count: number) => cjkText`${count}個のプロセスのリソース データを読み取れなかったため、今回の判定には含まれていません。`,
  resourceKind: {
    ProcessHandles: "プロセス ハンドル"
  },
  holder: {
    processId: "PID",
    productName: "製品名",
    services: "サービス",
    imagePath: "イメージ パス",
    noProductName: "提供されていません",
    noServices: "対応するサービスはありません",
    unavailable: "読み取れません",
    processEnded: "プロセスは終了しました"
  },
  evidence: {
    currentValue: "現在の値",
    growth: "観察期間中の増加",
    growthPerSecond: "増加率",
    observationTime: "観察時間",
    sampleCount: "サンプル数",
    firstObserved: "初回検出",
    lastObserved: "最終検出"
  },
  ratePerSecond: (value: string) => `${value}/秒`,
  actions: {
    title: "対処",
    name: {
      RestartHolder: "保持プロセスを再起動",
      ForceReleaseHandles: "ハンドルを強制解放"
    },
    riskLevel: {
      Warning: "警告",
      High: "高"
    },
    riskText: (level: string, risk: string) => `リスク: ${level}。${risk}`,
    riskLabel: (name: string) => cjkText`${name}のリスク`
  }
};
