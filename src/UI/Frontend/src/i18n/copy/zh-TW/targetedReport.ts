import type { AppCopy } from "../zh/index.ts";

export const zhTwTargetedReportCopy: Pick<AppCopy, "targetedReport"> = {
  targetedReport: {
    tab: "定向報告",
    title: "定向監控報告",
    intro: "記錄此軟體所有處理程序的應用程式提交影格時間與資源用量。",
    maximumSeconds: "最長錄製時間（秒）",
    start: "開始錄製",
    stop: "停止錄製",
    recording: "錄製中",
    completed: "已結束",
    refresh: "重新整理報告",
    empty: "尚無錄製報告。",
    loading: "正在讀取報告…",
    failed: "錄製報告操作失敗。",
    delete: "刪除報告",
    confirmDelete: "確定刪除這份錄製報告？",
    exportJson: "匯出 JSON",
    exportCsv: "匯出 CSV",
    frameTime: "影格時間隨時間變化",
    distribution: "影格率分布",
    resources: "資源曲線",
    noFrames: "沒有可用的影格間隔資料。",
    applicationPresent: "應用程式提交影格",
    incomplete: "錄製期間有資料缺口，統計可能偏低。",
    averageFps: "平均 FPS",
    onePercentLow: "1% Low",
    pointOnePercentLow: "0.1% Low",
    p50: "P50 影格時間",
    p95: "P95 影格時間",
    p99: "P99 影格時間",
    maxFrameTime: "最長影格時間",
    frameCount: "影格間隔數",
    software: "目標軟體",
    system: "整機",
    seconds: "秒",
    stopReasons: {
      user: "手動停止",
      processExit: "處理程序已結束",
      maxDuration: "已達最長錄製時間",
      interrupted: "服務中斷",
      captureError: "擷取出錯"
    }
  }
};
