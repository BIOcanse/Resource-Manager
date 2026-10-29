import { cjkText } from "../../cjkSpacing.ts";
export const zhRuntimeAndReportsCopy = {
  smartReport: {
    processTarget: (name: string, pid: number) => `${name}（處理程序 ${pid}）`,
    loading: "正在讀取",
    unavailable: "狀態不可用",
    refresh: "重新整理",
    completed: "已完成",
    needsAttention: "需要關注",
    releasedMemory: "記憶體釋放",
    releasedVram: "視訊記憶體釋放",
    empty: "暫無智慧排程記錄。",
    details: "詳細資訊",
    fallbackTarget: "排程",
    detailTitle: (target: string) => cjkText`${target}詳細資訊`,
    readFailed: "智慧排程報告讀取失敗",
    showAllRecords: (count: number) => `顯示全部 ${count} 筆記錄`,
    showRecentRecords: (count: number) => `只顯示最近 ${count} 筆`,
    section: {
      result: "排程結果",
      execution: "執行情況"
    },
    field: {
      target: "物件",
      change: "調整內容",
      affectedResource: "影響資源",
      state: "狀態",
      updatedAt: "更新時間",
      accepted: "成功",
      timedOut: "超時",
      rejected: "未執行",
      releasedMemory: "已釋放記憶體",
      releasedVram: "已釋放視訊記憶體"
    },
    itemCount: (count: number) => `${count} 項`,
    kind: {
      softwareResourceSettings: "軟體資源設定",
      gpuSelection: "顯示卡選擇",
      runtimeGpuSwitch: "執行時顯示卡切換",
      cpuCoreAllocation: "CPU 核心分配",
      resourceOptimization: "資源最佳化",
      schedulingAdjustment: "排程調整"
    },
    // 智能调度历史（docs/report_lifecycle_2026-10-28.md“智能调度记录”）。
    history: {
      title: "歷史記錄",
      loading: "正在讀取歷史記錄",
      readFailed: "智慧排程歷史讀取失敗",
      empty: "暫無歷史記錄。",
      retentionOff: "保留智慧排程歷史已關閉，記錄還原後不再保留。可在「設定 → 執行策略」中開啟。",
      loadMore: "載入更早的記錄",
      field: {
        sequence: "序號",
        recordedAt: "記錄時間",
        appliedAt: "套用時間",
        restoredAt: "還原時間",
        restoreResult: "還原結果"
      },
      restoreResult: {
        restored: "已還原",
        targetExited: "目標已結束",
        ownershipLost: "已被其他程式修改，不再還原",
        failed: "還原失敗",
        unknown: "結果未記錄"
      }
    },
    resource: {
      cpu: "處理器",
      gpu: "圖形處理器",
      memory: "記憶體",
      vram: "視訊記憶體",
      disk: "磁碟",
      network: "網路",
      software: "軟體資源"
    }
  },
  // 报告页的“清空历史”：一次删除这类报告的全部历史条目，活动条目保留（src/features/reportHistory）。
  reportHistory: {
    clear: "清除歷史",
    clearing: "正在清除",
    confirmTitle: "清除歷史項目？",
    confirm: "清除",
    confirmMessage: {
      optimizationReports: (count: string) => cjkText`將刪除${count}筆歷史報告，作用中的報告保留。`,
      kernelLeaks: (count: string) => cjkText`將刪除${count}筆歷史項目，作用中的項目保留。`,
      memoryProtection: (count: string) => cjkText`將刪除${count}筆記憶體保護處理記錄。`,
      targetedRecordings: (count: string) => cjkText`將刪除${count}份已完成的錄製報告，進行中的錄製保留。`,
      smartRecords: "將刪除全部智慧排程歷史記錄，待還原的記錄保留。"
    },
    details: {
      optimizationReports: ["刪除後無法復原；同一問題日後再次出現時會重新記錄。", "已信任的軟體和裝置仍維持信任。"],
      kernelLeaks: ["刪除後無法復原；同一問題日後再次出現時會重新記錄。"],
      memoryProtection: ["刪除後無法復原。", "轉儲的切片檔案不會刪除，仍留在磁碟保留區所在的磁碟上，需要時請自行處理。"],
      targetedRecordings: ["這些報告及其影格間隔與資源資料將被永久刪除，無法復原。"],
      smartRecords: ["刪除後無法復原；記錄序號不會重新開始。"]
    },
    clearedTitle: "已清除歷史",
    cleared: (count: string) => cjkText`已刪除${count}筆歷史項目。`,
    failed: "清除歷史失敗。"
  },
  browserRuntime: {
    description: "檢視 Web 介面執行環境和已安裝的瀏覽器。",
    downloadShared: "下載共享執行時",
    refreshing: "正在重新整理",
    refresh: "重新整理",
    sharedRuntimeTitle: "當前共享執行時",
    sharedRuntimeDescription: "資源管理器與其它 WebView2 軟體共用同一份系統執行時。",
    openOfficialSource: "開啟官方來源",
    noSharedRuntime: "未檢測到共享執行時",
    noSharedRuntimeDetail: "可以安裝官方共享執行時；現有瀏覽器仍會列入備用目錄。",
    inUse: "正在使用",
    installedBrowsersTitle: "已安裝的瀏覽器",
    installedBrowsersDescription: "支援外部瀏覽器的軟體可以使用這些瀏覽器。",
    itemCount: (count: number) => `${count} 項`,
    noBrowsers: "未發現可複用的瀏覽器。",
    currentFallback: "當前備用",
    fallbackRuntimeName: "執行時",
    detailTitle: (name: string) => cjkText`${name}詳細資訊`,
    version: (value: string) => `版本 ${value}`,
    unknown: "未知",
    detailsOf: (name: string) => cjkText`${name}詳細資訊`,
    sharedRuntimePurpose: "供本機 WebView2 軟體共同使用的共享介面執行時。",
    externalBrowserPurpose: "供支援外部瀏覽器的軟體開啟 Web 介面；不能作為 WebView2 嵌入執行時。",
    chromiumReusePurpose: "供支援外部 Chromium 瀏覽器的軟體複用。",
    runtimeInfoTitle: "執行時資訊",
    field: {
      kind: "型別",
      version: "版本",
      state: "狀態",
      source: "來源",
      runtimeDirectory: "執行目錄",
      executable: "可執行檔案"
    },
    available: "可用",
    kind: {
      webView2Runtime: "共享 WebView2 執行時",
      chromiumBrowser: "Chromium 瀏覽器",
      geckoBrowser: "Gecko 瀏覽器"
    },
    source: {
      systemMachine: "系統（全機）",
      systemUser: "系統（當前使用者）",
      managed: "資源管理器下載",
      browserRegistration: "系統瀏覽器註冊",
      knownInstall: "本機安裝目錄",
      local: "本機"
    }
  }
};