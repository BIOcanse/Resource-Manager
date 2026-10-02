import type { ResourceTableViewMode } from "../../../types.ts";

export const zhMonitorCopy = {
  monitorPage: {
    addCard: "新增卡片",
    addCardLabel: "新增監控卡片",
    cancel: "取消",
    cancelEditLabel: "取消監控面板佈局編輯",
    save: "儲存",
    edit: "編輯",
    saveLayoutLabel: "儲存監控面板佈局",
    editLayoutLabel: "編輯監控面板佈局",
    layoutEditUnavailable: "佈局編輯暫不可用",
    layoutEditor: "監控面板佈局編輯器",
    dashboardQuarantined: "儀表盤配置已損壞並隔離，當前使用明確儲存的安全預設配置。",
    dashboardRecovered: "儀表盤配置已從最近一次有效副本恢復。",
    metricCatalog: "指標目錄",
    liveMetrics: "實時指標",
    resourceUsage: "資源佔用",
    resourceList: "資源列表"
  },
  dashboard: {
    panel: "監控面板",
    emptyMetric: "空指標",
    primarySlot: (ordinal: number) => `第 ${ordinal} 張卡片主指標`,
    detailSlot: (ordinal: number, index: number) => `第 ${ordinal} 張卡片明細指標 ${index}`,
    addMetric: (slot: string) => `為${slot}新增指標`,
    replaceMetric: (slot: string, metric: string) => `更換${slot}：${metric}`,
    removeMetric: (slot: string, metric: string) => `刪除${slot}：${metric}`,
    catalogUnavailable: "指標目錄暫不可用",
    metricUnavailable: "指標不可用"
  },
  resourceBreakdown: {
    panel: "軟體資源佔用",
    save: "儲存",
    edit: "編輯",
    statusFallback: "暫無資料",
    scaleCapacity: "含空餘",
    scaleActive: "現有佔用",
    selectionLabel: (metric: string) => `${metric} 軟體佔用選擇`,
    empty: "空餘",
    emptyPercent: "空餘佔比",
    systemPercent: "系統佔比",
    categoryCount: (count: number) => `${count} 個分類`,
    processCount: (count: number) => `${count} 個程序`,
    noAttributableProcess: "無可歸因程序",
    noPidCategory: "無 PID 分類",
    providerNotSplit: "系統共享佔用，無法歸到單個程序。",
    etwSupplement: "已識別到程序，但尚未歸到具體軟體。",
    softwareInnerPercent: "軟體內佔比",
    memoryBasis: "整機：實體記憶體實際用量 / 物理容量。程序：駐留工作集，含共享頁；跨程序求和可能重複計算共享頁。",
    commitBasis: "整機：提交量 / 當前提交上限。程序：私有提交量。提交量包含實體記憶體與頁面檔案後備，不是頁面檔案實際佔用。",
    vramBasis: "裝置：視訊記憶體實際駐留量 / 物理容量。程序：實際駐留量；共享部分按引用程序分攤。",
    gpuUsageBasis: "裝置：實測佔用率。軟體和程序：按 GPU 引擎活動佔比分攤的佔用率。",
    gpuUsageLabel: (index: string) => `GPU${index} 佔用率`,
    gpuVramLabel: (index: string) => `GPU${index} 視訊記憶體（實際駐留）`
  },
  resourceTable: {
    panel: "資源列表",
    performance: "效能",
    searchPlaceholder: "搜尋名稱、PID、狀態",
    viewLabel: "資源列表檢視",
    save: "儲存",
    edit: "編輯",
    expandProcesses: "展開程序",
    collapseProcesses: "收起程序",
    modes: {
      software: "軟體級",
      process: "程序級",
      performance: "效能"
    } as Record<ResourceTableViewMode, string>,
    summaryRow: {
      name: "總佔用",
      status: {
        warming: "正在預熱",
        ready: "當前取樣",
        stale: "上一次取樣",
        failed: "取樣失敗"
      }
    },
    systemResidualRow: (metric: string) => `系統/驅動保留 · ${metric}`,
    columns: {
      name: "名稱",
      pid: "PID",
      status: "狀態",
      user: "使用者",
      architecture: "架構",
      cpu: "CPU",
      memory: "記憶體（駐留）",
      disk: "磁碟",
      network: "網路"
    },
    gpuUsageLabel: (index: string) => `GPU${index} 佔用率`,
    gpuVramLabel: (index: string) => `GPU${index} 視訊記憶體（實際駐留）`
  }
};
