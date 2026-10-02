export const zhRuntimeAndReportsCopy = {
  smartReport: {
    panel: "智慧排程報告",
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
    detailTitle: (target: string) => `${target}詳細資訊`,
    readFailed: "智慧排程報告讀取失敗",
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
  browserRuntime: {
    title: "執行時管理",
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
    detailTitle: (name: string) => `${name}詳細資訊`,
    version: (value: string) => `版本 ${value}`,
    unknown: "未知",
    details: "詳細資訊",
    detailsOf: (name: string) => `${name}詳細資訊`,
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