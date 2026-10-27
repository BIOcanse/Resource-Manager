import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "カーネル リーク レポート",
  intro: "カーネル リソース（現在はプロセス ハンドル）が増え続け、際限なく増加している疑いのあるプロセスと、その判定根拠を一覧表示します。判定結果は疑いを示すだけです。対処するかどうかは判定根拠に基づいて決定してください。観察はこのページの表示中にのみ行われ、ページを開き直すと最初からやり直します。",
  loading: "カーネル リーク レポートを読み込んでいます…",
  readFailed: "カーネル リーク レポートを読み込めませんでした",
  capturedAt: "取得日時",
  suspectedItems: "疑いのある項目",
  emptyTitle: "カーネル リークの疑いは見つかりませんでした",
  emptyDetail: "観察時間が判定に足りない間もこの結果が表示されます。疑いのある項目が見つかると、ここに表示されます。",
  notObservedTitle: "最初のサンプルはまだ取得されていません",
  unavailableTitle: "カーネル リーク レポートを利用できません",
  failure: {
    NotObserved: "観察を開始しました。最初のサンプルの取得後に結果を表示します。",
    SourceUnavailable: "プロセスのリソース データを読み取れません。",
    CapacityExceeded: "観察するプロセスの数が容量の上限を超えたため、今回の観察を停止しました。",
    InvalidObservation: "サンプル データが無効なため、今回の観察を停止しました。"
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
    currentValue: "現在の数",
    growth: "観察期間中の増加",
    growthPerSecond: "増加率",
    observationTime: "観察時間",
    sampleCount: "サンプル数"
  },
  ratePerSecond: (value: string) => `${value}/秒`,
  actionsPending: "この項目の対処操作は、ローカル サービスからまだ提供されていません。"
};
