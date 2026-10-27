import type { AppCopy } from "../index.ts";

export const smartFailure: AppCopy["smartFailure"] = {
  title: {
    "cycle-failed": "このスケジューリング区間は失敗しました",
    "native-plan-failed": "ネイティブのスケジューリング計画は失敗しました"
  },
  hint: {
    "cycle-failed": "このスケジューリング区間の実行中にエラーが発生し、完了しませんでした。後の区間が正常に完了すると、この通知は消えます。エラーの詳細はローカルサービスのログに記録されています。",
    "native-plan-failed": "ネイティブのスケジューリングコンポーネントがこの区間の計画を作成できなかったため、この区間では調整を実行していません。計画の状態に原因が示されています。後の区間が正常に完了すると、この通知は消えます。"
  },
  nativePlanStatus: (status: string) => `計画の状態: ${status}`,
  nativePlanStatuses: {
    "invalid-argument": "引数が無効です",
    "abi-mismatch": "ネイティブコンポーネントのインターフェースのバージョンが一致しません",
    unavailable: "ネイティブコンポーネントを利用できません",
    "no-data": "利用できるデータがありません",
    "buffer-too-small": "バッファーの容量が不足しています",
    "stale-generation": "入力データが古くなっています",
    "out-of-memory": "メモリが不足しています",
    "capacity-exceeded": "容量の上限を超えました",
    "conflicting-facts": "入力データが互いに矛盾しています",
    "feedback-mismatch": "実行結果の通知が計画と一致しません",
    "recreate-required": "ネイティブの作業領域を作り直す必要があります"
  },
  exclusionTotal: (count: number) => `この区間で除外されたデータ: ${count} 件`,
  exclusionsHint: "除外されたデータとは、欠落している、無効である、または互いに矛盾しているため、この区間のスケジューリングの判断に使われなかったデータです。集計はデータ項目と理由ごとに件数を示し、詳細は対象を示します。",
  summaryLabel: "除外の集計",
  viewExclusions: "除外の詳細",
  detailTitle: "この区間で除外されたデータ",
  detailSummary: (listed: number, total: number) => `除外は合計 ${total} 件で、以下に ${listed} 件を示します。`,
  field: {
    processId: "プロセス ID",
    processStartKey: "プロセス起動識別子",
    targetId: "スケジューリング対象の識別子",
    target: "スケジューリング対象"
  },
  noTarget: "特定の対象に関するものではありません",
  facts: {
    inventory: "プロセス一覧",
    identity: "プロセスの識別情報",
    "base-score": "基本スコア",
    attribution: "ソフトウェアへの帰属",
    "runtime-state": "スケジューリング対象の実行状態",
    "cpu-core-residency": "CPU コアへの常駐",
    "cpu-score": "CPU スコア",
    "gpu-score": "GPU スコア",
    "compute-score": "演算スコア",
    "memory-cleanup": "メモリのクリーンアップ",
    "decision-target": "判断の対象",
    "native-plan": "ネイティブのスケジューリング計画",
    CpuUsage: "プロセスの CPU 使用率",
    MemoryUsage: "プロセスのメモリ使用量",
    GpuUsage: "プロセスの GPU 使用率",
    GpuDedicatedMemory: "プロセスの専用 VRAM 使用量",
    RuntimeState: "プロセスの実行状態"
  },
  reasons: {
    "dataset-not-current": "データが現在のサンプルではありません",
    "unidentified-live-process": "識別できない実行中のプロセスがあります",
    "inventory-overflow": "プロセス一覧が容量を超えました",
    "emitted-count-mismatch": "出力件数がプロセス一覧と一致しません",
    "inventory-count-mismatch": "一覧の件数が列挙した数と一致しません",
    "capture-count-mismatch": "取得件数が一致しません",
    "invalid-or-duplicate-inventory-identity": "プロセス一覧に無効または重複したプロセスの識別情報があります",
    "invalid-or-duplicate-process": "プロセスの識別情報が無効または重複しています",
    "row-missing-or-invalid": "データが欠落しているか無効です",
    "gpu-row-missing-or-invalid": "GPU のデータが欠落しているか無効です",
    "compute-runtime-state-missing": "実行状態がありません",
    "cpu-residency-not-covered": "CPU 常駐のサンプリングがこのプロセスを対象にしていません",
    "cpu-residency-invalid": "CPU 常駐のデータが無効です",
    "target-process-count-not-one": "スケジューリング対象に対応するプロセスが 1 つではありません",
    "target-process-identity-missing": "スケジューリング対象にプロセスの識別情報がありません",
    "target-process-identity-mismatch": "スケジューリング対象のプロセスの識別情報が一致しません",
    "duplicate-target-process-identity": "複数のスケジューリング対象が同じプロセスを指しています",
    "target-runtime-state-invalid": "スケジューリング対象の実行状態が無効です",
    "cleanup-baseline-score-missing": "クリーンアップの基準スコアがありません",
    "cleanup-current-process-facts-missing": "プロセスの現在のデータがありません",
    "cleanup-software-maximum-base-score-missing": "ソフトウェアの最高基本スコアがありません",
    "ownership-software-attribution-mismatch": "変更の所有記録がソフトウェアへの帰属と一致しません",
    "canonical-process-membership-mismatch": "プロセスがスコアのソフトウェア構成と一致しません",
    "canonical-software-score-missing": "ソフトウェアのスコアがありません",
    "live-identity-without-decision-facts": "実行中のプロセスに判断用のデータがありません",
    "canonical-process-not-projected": "プロセスのスコアがスケジューリングの入力に反映されていません",
    "canonical-software-membership-incomplete": "ソフトウェアのスコアの構成が不完全です"
  }
};
