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
        amdSmuPawnIo: "AMD 處理器感測支援",
        amdRyzenMaster: "AMD Ryzen 監控支援",
        msiAfterburner: "MSI Afterburner",
        libreHardwareMonitor: "通用硬體感測支援",
        sharedWebView2Runtime: "共享 WebView2 執行時",
        notebookFanControl: "筆記本風扇監控支援",
        notebookOemFan: "筆記本廠商風扇支援",
        windowsPerformanceToolkit: "Windows 效能分析工具",
        latencyMon: "LatencyMon",
        nvidiaNvml: "NVIDIA 顯示卡監控支援",
        nvidiaNvapi: "NVIDIA 顯示卡擴充套件監控支援",
        amdAdlx: "AMD 顯示卡監控支援",
        intelPcm: "Intel 處理器監控支援"
      },
      purposes: {
        msiAfterburner: "裝了 Afterburner 就順便讀它的顯示卡資料，不裝也不影響。",
        amdRyzenSdk: "AMD 官方 SDK，讀功耗、電壓、電流和溫度。",
        amdSmu: "直接讀 SMU 資料表，比官方 SDK 多出 STAPM 和每核讀數。",
        intelCpu: "讀 Intel 處理器的功耗、頻率和溫度。",
        generalHardwareSensors: "主機板、風扇、溫度、電壓的通用來源，多數機器夠用。",
        nvidiaNvml: "從顯示卡驅動讀頻率、視訊記憶體、功耗和溫度。",
        nvidiaNvapi: "補上驅動基礎介面沒給的風扇、電壓和電流。",
        amdGpu: "讀 AMD 顯示卡的頻率、溫度、功耗和風扇。",
        notebookEcFan: "從筆記本 EC 讀風扇，別的路子都讀不到時用它。",
        notebookOemFan: "走廠商驅動讀 CPU/GPU 風扇，認得出的機型更準。",
        latencyMon: "排查中斷延遲和卡頓，看得出是哪個驅動在拖後腿。",
        windowsPerformanceToolkit: "微軟官方的 WPR / Xperf，做更深的系統級追蹤。",
        sharedWebView2Runtime: "介面用的 Chromium 執行時，全機共享。"
      },
      categories: {
        performanceAnalysis: "效能分析工具",
        helperTool: "輔助工具",
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
