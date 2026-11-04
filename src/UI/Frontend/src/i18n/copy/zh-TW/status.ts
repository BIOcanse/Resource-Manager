export const zhStatusCopy = {
  status: {
    sentenceEnd: "。",
    actionFailed: "操作失敗，請稍後重試",
    stateUpdated: "狀態已更新",
    unknownState: "狀態未知",
    noData: "暫無資料",
    unknownTime: "時間未知",
    systemGraphicsUsage: "系統圖形佔用",
    // 后端状态值统一映射到这些用户状态
    state: {
      payloadMissing: "已安裝，執行檔案缺失",
      updateAvailable: "有可用更新",
      available: "可用",
      installed: "已安裝",
      installedUnverified: "已安裝，等待確認",
      readyToInstall: "可安裝",
      downloadable: "可下載",
      manualDownload: "需要手動下載",
      notInstalled: "未安裝",
      running: "執行中",
      stopped: "已停止",
      starting: "正在啟動",
      ready: "已就緒",
      refreshing: "正在重新整理",
      warming: "正在準備",
      failed: "操作失敗",
      unavailable: "暫時不可用",
      disabled: "已關閉",
      enabled: "已開啟",
      queued: "等待中",
      canceling: "正在取消",
      canceled: "已取消",
      completed: "已完成",
      restored: "已恢復",
      missing: "未找到",
      unknown: "狀態未知"
    },
    risk: {
      low: "低風險",
      medium: "中等風險",
      high: "高風險",
      root: "根目錄遷移",
      blocked: "禁止遷移",
      unknown: "風險待確認"
    },
    migration: {
      kindRoot: "軟體根目錄",
      kindData: "軟體資料",
      targetMisc: "其他資料",
      targetUser: "使用者資料",
      classificationApplicationRoot: "軟體根目錄",
      classificationUserAppData: "使用者應用資料",
      classificationProgramData: "共享應用資料",
      classificationDirectory: "普通目錄",
      classificationFile: "檔案",
      classificationMissing: "源位置不存在",
      classificationUnknown: "待確認資料",
      stateRunning: "監控中",
      stateStopped: "已停止",
      stateCompleted: "已完成",
      stateRestored: "已恢復",
      stateFailed: "未完成",
      stateUnknown: "狀態未知"
    },
    metricGroup: {
      cpu: "處理器",
      gpu: "圖形處理器",
      memory: "記憶體",
      virtualMemory: "虛擬記憶體",
      disk: "磁碟",
      network: "網路",
      motherboard: "主機板",
      fan: "風扇",
      hardwareMonitor: "硬體感測器",
      system: "系統"
    },
    metricUnavailable: {
      needsComponent: "需要裝上硬體支援元件",
      deviceNotProvided: "不支援讀取"
    },
    component: {
      nameFallback: "硬體支援元件",
      purposeFallback: "為資源管理器補充硬體資訊和相關功能。",
      names: {
          fanControlCore: "風扇控制核心",
          hardwareBridge: "硬體寫入輔助程序",
        amdSmuPawnIo: "AMD 處理器感測支援",
        hardwareSensorsPawnIo: "硬體感測驅動程式（PawnIO）",
        intelMsrPawnIo: "Intel 處理器控制",
        libreHardwareMonitor: "主機板與記憶體感測支援",
        sharedWebView2Runtime: "共享 WebView2 執行時",
        notebookOemFan: "筆記本廠商風扇支援",
        nvidiaNvml: "NVIDIA 顯示卡監控支援",
        nvidiaNvapi: "NVIDIA 顯示卡擴充套件監控支援",
        amdAdlx: "AMD 顯示卡監控支援",
      },
      purposes: {
          fanControlCore: "讀取並控制機身風扇，執行韌體或軟體曲線。",
          hardwareBridge: "調整處理器功耗上限與電壓，撤銷時恢復保存的原值。",
        amdSmu: "直接讀處理器的 SMU 資料表，提供 STAPM、功耗、電壓、電流、溫度和每核讀數。",
        hardwareSensorsPawnIo: "安裝官方簽署的 PawnIO 驅動程式，供主機板和記憶體感測使用；不會靜默安裝。",
        intelMsrPawnIo: "透過官方簽署的 IntelMSR 模組調整 PL1/PL2、時間窗和電壓偏移；每次寫入都回讀核對，釋放時恢復原值。",
        motherboardMemorySensors: "隨產品提供的核心庫，讀主機板溫度、電壓、風扇和記憶體溫度；需先裝硬體感測驅動程式。",
        nvidiaNvml: "從顯示卡驅動讀頻率、視訊記憶體、功耗和溫度。",
        nvidiaNvapi: "補上驅動基礎介面沒給的風扇、電壓和電流。",
        amdGpu: "讀 AMD 顯示卡的頻率、溫度、功耗和風扇。",
        notebookOemFan: "走廠商驅動讀 CPU/GPU 風扇，認得出的機型更準。",
        sharedWebView2Runtime: "介面用的 Chromium 執行時，全機共享。"
      },
      categories: {
        hardwareMonitoring: "硬體監控"
      }
    },
    resourceData: {
    },
    detail: {
      status: "狀態",
      description: "說明",
      software: "軟體",
      unnamedSoftware: "未命名軟體",
      content: "內容",
      savedLocation: "儲存位置",
      createdAt: "建立時間",
      restoredAt: "恢復時間",
      migrationContent: "遷移內容",
      location: "位置",
      sourcePath: "原位置",
      destinationPath: "遷移後位置"
    }
  }
};
