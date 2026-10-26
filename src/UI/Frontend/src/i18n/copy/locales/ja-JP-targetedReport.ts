import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "対象ソフトウェアの監視レポート", title: "対象ソフトウェアの監視レポート",
  chooseSoftware: "記録するソフトウェアを選択",
  noSoftware: "登録済みのソフトウェアがありません。先にソフトウェア管理で対象を登録してください。",
  oneAtATime: "同時に実行できる記録は1件です。現在の記録を先に停止してください。",
  workflow: "登録済みで実行中のソフトウェアを選んで記録します。ソフトウェア詳細の概要からも開始できます。既定は600秒で、1～3600秒に設定できます。手動停止、プロセス終了、制限時間到達でレポートを作成し、完了した記録を最新50件まで保存します。",
  intro: "このソフトウェアの全プロセスについて、アプリの Present 間隔とリソース使用量を記録します。",
  maximumSeconds: "記録の最長時間（秒）", start: "記録開始", stop: "記録停止",
  recording: "記録中", completed: "完了", refresh: "レポートを更新",
  empty: "記録済みレポートはありません。", loading: "レポートを読み込み中…",
  failed: "記録レポートの操作に失敗しました。", delete: "レポートを削除",
  confirmDelete: "この記録レポートを削除しますか？", exportJson: "JSON を出力", exportCsv: "CSV を出力",
  frameTime: "時間ごとのフレーム時間", distribution: "FPS 分布", resources: "リソースの推移",
  noFrames: "利用可能なフレーム間隔がありません。", applicationPresent: "アプリの Present フレーム",
  incomplete: "記録に欠損があり、統計値が低くなる可能性があります。",
  averageFps: "平均 FPS", onePercentLow: "1% Low", pointOnePercentLow: "0.1% Low",
  p50: "P50 フレーム時間", p95: "P95 フレーム時間", p99: "P99 フレーム時間",
  maxFrameTime: "最大フレーム時間", frameCount: "フレーム間隔数",
  software: "対象ソフトウェア", system: "システム", seconds: "秒",
  stopReasons: { user: "手動停止", processExit: "プロセス終了", maxDuration: "最長時間に到達",
    interrupted: "サービス中断", captureError: "記録エラー" }
};
