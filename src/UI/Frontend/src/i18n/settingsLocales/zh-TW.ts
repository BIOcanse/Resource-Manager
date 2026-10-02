import { createSettingsLocale } from "../settingsLocaleFactory.ts";
export default createSettingsLocale("zh-TW", {
  "updates": {
    "autoUpdateTitle": "自動更新",
    "autoUpdateDescription": "儲存自動更新偏好。可信釋出簽名接入前不會自動安裝；手動更新可用。",
    "installedVersionTitle": "當前安裝版本",
    "unknownVersion": "無法識別",
    "checking": "檢查中…",
    "checkForUpdates": "檢查更新",
    "catalogFailed": "無法讀取版本目錄：",
    "historyIncomplete": "版本歷史尚未完整讀取。",
    "historyStale": "正在顯示上次讀取的版本目錄；請聯網後重試。",
    "versionsTitle": "產品版本",
    "versionsDescription": "從新到舊排列；每個系列預設顯示最新一版。",
    "lastChecked": "上次檢查：",
    "selectedTarget": "已選擇目標版本：",
    "prepare": "準備並安裝所選更新",
    "confirmPrepare": (version) => `將更新到 ${version}。更新器會校驗發行包，等待你退出所有桌面介面，停止服務，保留並遷移資料，驗證新版本；失敗時恢復舊版。繼續準備嗎？`,
    "prepareFailed": "無法準備更新。",
    "waitingForExit": "更新包已準備。請從托盤退出所有 Resource Manager 桌面介面；更新器會隨後切換版本。",
    "stageLabel": "更新狀態",
    "unknownStage": "未知更新狀態",
    "stages": {
      "idle": "空閒",
      "checking": "檢查更新",
      "downloading": "下載發行包",
      "verifying": "校驗發行包",
      "waitingForExit": "等待桌面介面退出",
      "completed": "更新完成",
      "error": "更新失敗"
    },
    "otherVersions": "其他版本",
    "installedUnknown": "無法確認已安裝版本，不能判斷升級方向。",
    "stable": "穩定版",
    "preview": "預覽版",
    "noLatest": "暫無最新版本。",
    "noStable": "暫無穩定版本。",
    "latest": "最新版本",
    "latestStable": "最新穩定版本",
    "verified": "已驗證版本",
    "unavailableReasons": {
      "无法确认当前安装版本，升级选择已关闭。": "無法確認當前安裝版本，升級選擇已關閉。",
      "只能选择高于当前安装版本的发行版。": "只能選擇高於當前安裝版本的發行版。"
    }
  },
  "navigationLabel": "設定分割槽",
  "sections": {
    "performance": "效能",
    "appearance": "外觀",
    "systemIntegration": "系統整合",
    "updates": "更新與版本",
    "debug": "偵錯",
    "credits": "致謝"
  },
  "saveState": {
    "saving": "儲存中",
    "saved": "已儲存",
    "constrained": "已儲存；當前執行模式已限制不適用項",
    "partial": "已儲存，但尚未完全應用",
    "error": "儲存失敗",
    "dirty": "未儲存",
    "conflict": "設定已在其他位置更新，請重新載入"
  },
  "loadState": {
    "loading": "正在讀取已儲存設定…",
    "errorTitle": "無法讀取設定",
    "errorDescription": "沒有顯示或使用預設值替代真實設定。請恢復本地服務後重試。",
    "staleTitle": "設定已失去同步",
    "staleDescription": "當前副本可能過期，重新載入成功前不會提交更改。"
  },
  "actions": {
    "save": "儲存",
    "restoreDefaults": "恢復為預設",
    "retry": "重試",
    "reload": "重新載入",
    "reapply": "重新應用"
  },
  "performance": {
    "smartMonitoringTitle": "按需監控",
    "smartMonitoringDescription": (seconds) => `只在需要顯示資料時保持對應監控；連續 ${seconds} 秒未使用後停止。`,
    "adaptiveBooleanModeOptions": [
      {
        "id": "auto",
        "label": "自動排程",
        "description": "按效能模式自動選。"
      },
      {
        "id": "enabled",
        "label": "始終開啟"
      },
      {
        "id": "disabled",
        "label": "始終關閉"
      }
    ],
    "gpuPerformanceUseCasesTitle": "GPU 用途",
    "gpuPerformanceUseCasesDescription": "選擇主要用途，以更合適地比較不同顯示卡的能力。可多選。",
    "preciseGpuPlacementTitle": "精確 GPU 選擇",
    "preciseGpuPlacementDescription": "允許受支援的渲染程序精確選卡；執行時換卡仍取決於該程序的相容能力。每個軟體可單獨設。",
    "preciseGpuPlacementModeOptions": {
      "basic": {
        "label": "關閉",
        "description": "僅使用 Windows 支援的顯示卡偏好。"
      },
      "precise": {
        "label": "開啟",
        "description": "允許為軟體精確選擇目標顯示卡。"
      }
    },
    "automaticMemoryCleanupTitle": "自動清理閾值",
    "automaticMemoryCleanupDescription": "剩餘低於這個值就啟動後臺清理。",
    "physicalMemoryAutomaticCleanupLabel": "實體記憶體剩餘 %",
    "virtualMemoryAutomaticCleanupLabel": "提交額度剩餘 %",
    "memoryOptimizationTargetTitle": "記憶體最佳化目標",
    "memoryOptimizationTargetDescription": "超過目標後慢慢釋放，不做緊急清理。",
    "physicalMemoryOptimizationTargetLabel": "實體記憶體目標使用率 %",
    "virtualMemoryOptimizationTargetLabel": "提交量目標使用率 %",
    "pauseHiddenTitle": "視窗隱藏時重新整理",
    "pauseHiddenDescription": "普通模式下可以暫停隱藏視窗的介面更新；重新顯示後立即更新。",
    "frontendHiddenRefreshModeOptions": [
      {
        "id": "auto",
        "label": "自動排程",
        "description": "按效能模式自動決定。"
      },
      {
        "id": "pauseWhenHidden",
        "label": "隱藏時暫停"
      },
      {
        "id": "continueWhenHidden",
        "label": "隱藏也重新整理"
      }
    ],
    "refreshCadenceTitle": "資訊更新頻率",
    "refreshCadenceDescription": "越快越費資源。",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "自動排程",
        "description": "按效能模式和視窗狀態自動選。"
      },
      {
        "id": "preset",
        "label": "鎖定預設",
        "description": "用右側選的預設。"
      },
      {
        "id": "custom",
        "label": "自定義",
        "description": "用手填的毫秒值。"
      }
    ],
    "refreshCadencePresetLabel": "預設",
    "refreshCadenceCustomLabel": "自定義 ms",
    "refreshCadencePresetOptions": [
      {
        "id": "responsive",
        "label": "響應",
        "description": "前臺互動優先，重新整理更快。"
      },
      {
        "id": "balanced",
        "label": "平衡",
        "description": "日常用這個。"
      },
      {
        "id": "lowPower",
        "label": "低功耗",
        "description": "後臺省電。"
      },
      {
        "id": "quiet",
        "label": "靜默",
        "description": "最低頻率，只留必要的。"
      }
    ],
    "refreshCadenceItems": {
      "monitor": {
        "label": "監控快照 / 資源條",
        "description": "監視控制檯的數值和資源條"
      },
      "resourceTable": {
        "label": "資源表",
        "description": "軟體和程序列表"
      },
      "management": {
        "label": "元件與軟體",
        "description": "元件狀態和操作進度"
      },
      "discovery": {
        "label": "軟體遷移",
        "description": "遷移進度和可遷移內容"
      },
      "optimization": {
        "label": "效能最佳化",
        "description": "最佳化報告和排程狀態"
      },
      "localSystem": {
        "label": "本機狀態",
        "description": "開機時間等低頻狀態"
      }
    }
  },
  "appearance": {
    "themeTitle": "色彩模式",
    "themeDescription": "選擇跟隨系統、淺色、深色或低對比外觀。",
    "animationTitle": "動畫效果",
    "animationDescription": "選擇完整動畫、關閉動畫，或進一步簡化視覺以降低介面開銷。",
    "resourceBarHardwareAccelerationTitle": "資源條硬體加速",
    "resourceBarHardwareAccelerationDescription": "使用顯示卡提升資源條動畫流暢度；遇到顯示異常時可以關閉。",
    "resourceBarHardwareAccelerationModeOptions": [
      {
        "id": "auto",
        "label": "自動排程",
        "description": "前臺流暢，後臺省開銷。"
      },
      {
        "id": "enabled",
        "label": "始終開啟",
        "description": "資源條走硬體加速。"
      },
      {
        "id": "disabled",
        "label": "始終關閉",
        "description": "資源條走相容渲染。"
      }
    ],
    "barColorTitle": "條形圖配色",
    "barColorDescription": "按軟體型別使用固定顏色，或為每個軟體分配不同顏色。",
    "byteUnitTitle": "容量單位",
    "byteUnitDescription": "1 GiB = 1024 MiB，1 GB = 1000 MB。切換後所有容量數字一起變，不只是改標籤。",
    "fontSmoothingTitle": "文字平滑",
    "fontSmoothingDescription": "自動平衡文字清晰度和介面開銷，也可以固定渲染方式。",
    "languageTitle": "介面語言",
    "languageDescription": "選擇 Resource Manager 介面使用的語言。跟隨系統會使用 Windows 顯示語言。",
    "settingsLanguageTitle": "介面語言",
    "settingsLanguageDescription": "切換整個介面的語言，包括桌面外殼的托盤與視窗標題；跟隨系統會使用 Windows 顯示語言。",
    "languageSelectLabel": "語言",
    "themeOptions": {
      "system": "跟隨系統",
      "light": "淺色",
      "dark": "深色",
      "lowContrast": "低對比"
    },
    "animationOptions": {
      "auto": {
        "label": "自動排程",
        "description": "前景使用正常動畫，低功耗背景自動切換為超高效能外觀。"
      },
      "normal": {
        "label": "正常動畫",
        "description": "啟用流暢的全域過渡動畫"
      },
      "none": {
        "label": "無動畫",
        "description": "關閉全部過渡與動畫，追求極致效能"
      },
      "ultra": {
        "label": "超高效能",
        "description": "關閉動畫並簡化視覺（去除陰影 / 高光 / 模糊），開銷最低"
      }
    },
    "barColorOptions": {
      "type": {
        "label": "型別固定色",
        "description": "按系統、適配和一般應用上色，相鄰分段用分隔線區分"
      },
      "distinct": {
        "label": "異色區分",
        "description": "每個軟體不同顏色，不表達型別，無分隔線"
      }
    },
    "byteUnitOptions": {
      "native": {
        "label": "按物理性質",
        "description": "記憶體、視訊記憶體、快取用 GiB（記憶體顆粒天生是 2 的冪）；磁碟容量、檔案大小、累計流量用 GB（與廠商標稱一致）"
      },
      "binary": {
        "label": "全用 GiB",
        "description": "一律按 1024 換算，標籤一律 KiB / MiB / GiB"
      },
      "decimal": {
        "label": "全用 GB",
        "description": "一律按 1000 換算，標籤一律 kB / MB / GB"
      }
    },
    "fontSmoothingOptions": {
      "auto": {
        "label": "自動排程",
        "description": "正常狀態使用系統 ClearType，超高效能狀態減少文字平滑開銷"
      },
      "system": {
        "label": "系統 ClearType",
        "description": "Windows 原生次畫素渲染，文字最銳利"
      },
      "grayscale": {
        "label": "灰度平滑",
        "description": "灰度抗鋸齒，更柔和，適合部分高分屏"
      },
      "disabled": {
        "label": "關閉",
        "description": "關閉額外文字平滑，使用最直接的字形柵格化"
      }
    },
    "gpuPerformanceUseCaseOptions": {
      "general": {
        "label": "綜合",
        "description": "光柵基礎分始終疊加小幅 GPU 代際加成"
      },
      "ai": {
        "label": "AI",
        "description": "增大代際加成，並重點參考 NVIDIA 生態、視訊記憶體容量和視訊記憶體速度"
      },
      "gaming": {
        "label": "遊戲",
        "description": "獲得更高的代際加成，並小幅參考視訊記憶體容量"
      }
    }
  },
  "systemIntegration": {
    "taskManagerTitle": "工作管理員快捷鍵替換",
    "autoStartTitle": "開機自啟",
    "autoStartDescription": "開機後執行後臺服務，登入後進入托盤。",
    "taskManagerDescription": "接管 Ctrl+Shift+Esc：註冊成功時未執行也會啟動 Resource Manager，已執行時顯示或置頂主視窗；不接管 Ctrl+Alt+Del。",
    "forceTerminateTitle": "強制結束快捷鍵",
    "forceTerminateDescription": "結束目前前景焦點程式，以及所有具有可見無回應視窗的程式。預設關閉。",
    "forceTerminateWarning": "觸發後會立即強制結束處理程式，未儲存的內容將遺失。啟用時必須同時包含 Ctrl、Alt 或 Win 修飾鍵以及一個非修飾鍵。",
    "hotkeyEmpty": "尚未設定按鍵",
    "hotkeyAddKey": "新增按鍵",
    "hotkeyKeyLabel": "按鍵",
    "hotkeyRemoveKey": "刪除按鍵",
    "hotkeyUnordered": "同時按下",
    "hotkeyOrdered": "前鍵先按",
    "publicServiceTitle": "本機公共服務",
    "publicServiceDescription": "允許本機軟體使用資源管理器提供的共享功能。",
    "publicFileIndexTitle": "共享軟體檔案索引",
    "publicFileIndexDescription": "複用資源管理器的現有索引，不會因查詢重新掃盤。",
    "publicDatabaseServiceTitle": "SQLite 資料庫服務",
    "publicDatabaseServiceDescription": "為本機程式提供相互隔離的命名資料庫、引數查詢和批處理事務。",
    "publicAiModelCatalogTitle": "統一 AI 模型庫",
    "publicAiModelCatalogDescription": "由資源管理器統一管理模型目錄和排程，LM Studio 負責執行模型。",
    "lmStudioEndpointTitle": "LM Studio 地址",
    "lmStudioEndpointDescription": "只接受本機 HTTP 地址。呼叫方始終使用資源管理器公共介面。",
    "lmStudioAutoStartTitle": "按需啟動 LM Studio",
    "lmStudioAutoStartDescription": "有軟體請求模型且 LM Studio 未執行時自動啟動；不會自動載入模型。",
    "aiGatewayTitle": "本地 AI 相容金鑰",
    "aiGatewayDescription": "為不直接支援 LM Studio 的軟體生成 OpenAI 或 Anthropic 相容金鑰；請求只會傳送給本機執行的開源模型。",
    "aiGatewayOpenAiProfile": "OpenAI 相容",
    "aiGatewayAnthropicProfile": "Anthropic 相容",
    "aiGatewayNamePlaceholder": "用途名稱（可選）",
    "aiGatewayGenerate": "生成金鑰",
    "aiGatewayGenerating": "處理中",
    "aiGatewayOneTimeTitle": "金鑰已生成",
    "aiGatewayOneTimeDescription": "金鑰只顯示一次，請立即儲存。之後無法再次檢視。",
    "aiGatewayApiKeyLabel": "API Key",
    "aiGatewayBaseUrlLabel": "Base URL",
    "aiGatewayCopy": "複製",
    "aiGatewayRevoke": "撤銷金鑰",
    "aiGatewayEmpty": "尚未生成相容金鑰。",
    "aiGatewayLoadFailed": "讀取本地 AI 金鑰失敗"
  },
  "debug": {
    "debugModeTitle": "偵錯模式",
    "debugModeDescription": "啟用本機偵錯功能。只在排查問題時開啟。",
    "debugLogTitle": "儲存偵錯日誌",
    "debugLogDescription": "記錄排查問題所需資訊，可能增加少量磁碟與處理器佔用。",
    "hostManagerSmartCoordinatorScoreOnlyTitle": "僅分析，不執行最佳化",
    "hostManagerSmartCoordinatorScoreOnlyDescription": "繼續監控並產生排程結果，但不實際變更軟體或硬體狀態。",
    "hostManagerSmartCoordinatorPerformanceLogTitle": "記錄排程效能",
    "hostManagerSmartCoordinatorPerformanceLogDescription": "記錄排程耗時與負載，用於排查 Resource Manager 自身開銷。"
  },
  "credits": {
    "heroTitle": "授權與鳴謝",
    "heroBody": "Resource Manager 使用的開源專案、執行環境、硬體支援和主要貢獻。",
    "dependencyListTitle": "完整依賴鳴謝",
    "dependencyListBody": "感謝以下所有包的作者與維護者。名單來自前端鎖檔案和實際解析的 .NET 包，含構建、測試和可選依賴；列出不代表當前機器載入或分發了所有包。",
    "groups": {
      "project": "專案與貢獻",
      "runtime": "應用程式執行階段與建置工具",
      "windows": "Windows 與診斷介面",
      "data": "離線資料",
      "build": "構建工具",
      "hardware": "硬體監控與可選支援"
    },
    "roles": {
      "project": "本專案",
      "frontendFramework": "前端 UI 框架",
      "icons": "介面圖示",
      "serialization": "Solid 的序列化依賴",
      "webView": "桌面網頁容器",
      "database": "本地資料庫",
      "nativeCompiler": "原生核心編譯器",
      "softwareCatalog": "軟體後設資料來源",
      "buildToolchain": "前端構建工具鏈",
      "typeSystem": "前端型別系統",
      "buildRuntime": "構建期執行時與型別定義",
      "dotnetRuntime": "本地應用執行環境",
      "hookLibrary": "Windows 相容支援",
      "startupInjectionLibrary": "軟體啟動相容支援",
      "traceLibrary": "系統效能分析",
      "windowsManagement": "Windows 系統資訊",
      "etwToolkit": "Windows 效能分析工具",
      "nvidiaTelemetry": "NVIDIA GPU 遙測",
      "nvidiaExtension": "NVIDIA GPU 擴充套件介面",
      "amdTelemetry": "AMD GPU/iGPU 遙測",
      "amdCpuSdk": "AMD CPU 感測 SDK",
      "intelTelemetry": "Intel CPU / MSR 遙測",
      "amdSmuBoundary": "AMD SMU 訪問邊界",
      "hardwareBridge": "通用硬體感測橋",
      "notebookEc": "筆記本風扇支援",
      "externalReference": "外部遙測參考 / 可選輔助",
      "latencyDiagnostics": "延遲診斷輔助",
      "deviceIdDatabase": "硬體 ID 名稱資料庫"
    },
    "notes": {
      "windowsFoundation": "感謝 Microsoft 與 Windows 工程團隊提供 Win32、Shell、COM、Direct3D/DXGI、PDH、ETW、服務、密碼和網路介面。這些由作業系統提供，不是另行捆綁的軟體。",
      "microsoftTools": "特別感謝 .NET SDK、MSBuild、NuGet、PowerShell 的作者與維護者，為個人開發提供編譯、釋出、包管理和自動化工具。",
      "managedServices": "感謝 Microsoft 與 .NET 貢獻者提供服務生命週期整合與基礎 API。TypeExtensions 在當前目標中僅為解析包占位，不代表額外 DLL。",
      "nativeToolchain": "感謝 GCC 與 MinGW 的作者及維護者提供原生編譯與執行庫基礎。部分原生元件靜態連結 libgcc/libstdc++；具體分發許可另列，不因鳴謝省略核查。",
      "buildContributors": "感謝全部構建依賴的作者與維護者，包括 Solid 編譯與重新整理工具、原始碼對映工具、瀏覽器資料及小型基礎庫。下方完整名單保留鎖檔案中的每個包和版本，包括可選平臺依賴。",
      "testContributors": "感謝 Playwright、xUnit.net、Coverlet 與 Microsoft 測試平臺貢獻者，使自動化互動、迴歸測試和覆蓋率檢查成為可能。它們用於開發驗證，不是基礎產品執行依賴。",
      "openHardwareMonitor": "感謝 OpenHardwareMonitor 作者與貢獻者提供硬體感測器介面；可讀取已安裝外部程式的 WMI 資料，不意味著基礎包捆綁該程式。",
      "upstreamContributors": "同樣感謝 .NET 與 WebView2 原始第三方宣告中列出的全部作者，包括 ANTLR、Unicode、Mono、Brotli、LLVM 等標準、執行庫和演算法貢獻者。完整原文隨包保留；這不是逐項執行時 DLL 清單。",
      "resourceManager": "Copyright (c) 2026 BIOcanse。本專案採用 Apache-2.0；第三方元件保留各自的許可。",
      "solid": "用於構建介面與互動。",
      "lucide": "介面使用 lucide-solid；保留 Lucide 的 ISC 許可及 Feather 衍生圖示的 MIT 許可與作者資訊。",
      "seroval": "Solid 的傳遞依賴；保留 Alexis Munsayac 的 MIT 許可與版權資訊。",
      "webView2": "桌面容器使用 WebView2 SDK；系統 WebView2 Evergreen Runtime 由 Microsoft 獨立分發，按其許可使用。",
      "sqlite": "用於本地資料儲存。Microsoft.Data.Sqlite 為 MIT 許可，SQLitePCLRaw 3.x 為 Apache-2.0 許可；SourceGear 分發的 SQLite 核心由上游宣告為公有領域。",
      "zig": "用於編譯原生核心；編譯器不是使用者執行程式的前置依賴。",
      "softwareCatalog": "離線軟體目錄使用 WinGet 的 MIT 許可資料及 Wikidata 的 CC0 結構化資料；專案摘要和中文翻譯為人工整理。",
      "vite": "負責開發構建和 Solid 前端打包。",
      "typescript": "用於提高介面程式碼和設定模型的可靠性。",
      "node": "用於前端構建和開發工具。",
      "dotnet": "提供 Resource Manager 的本地執行環境。",
      "minHook": "感謝 Tsuda Kageyu、貢獻者與 HDE 作者 Vyacheslav Patkov，提供掛鉤和指令解碼基礎；完整原始許可證與署名隨包保留。",
      "detours": "Microsoft Research 開源專案，為軟體啟動相容功能提供支援；專案保留原始許可證與版權資訊。",
      "traceEvent": "用於讀取 Windows 系統效能資訊。",
      "systemManagement": "用於讀取 Windows 系統與硬體資訊。",
      "wpt": "獨立安裝的 Microsoft 診斷工具，用於進一步分析 Windows 效能問題；不隨基礎程式安裝。",
      "nvml": "用於讀取 NVIDIA 顯示卡頻率、視訊記憶體、功耗和溫度。",
      "nvapi": "用於補充 NVIDIA 顯示卡風扇、冷卻和電氣資訊。",
      "adlx": "用於讀取 AMD 顯示卡佔用、頻率、功耗、溫度和電壓。",
      "ryzenMaster": "用於補充 AMD 處理器功耗、電壓、電流和溫度資訊；安裝和許可由使用者明確確認。",
      "intelPcm": "用於補充 Intel 處理器功耗、頻率和溫度資訊。",
      "pawnIo": "官方簽名驅動路線，用於 AMD SMU PM table 只讀遙測；不會靜默安裝。",
      "libreHardwareMonitor": "用於 CPU/系統風扇、記憶體溫度、主機板溫度、VRM 溫度、晶片組溫度和電壓等可選監控項。",
      "notebookFanControl": "用於普通硬體監控和顯示卡驅動都不暴露風扇時的可選路線。",
      "afterburner": "作為外部對照和可選輔助工具，不是 Resource Manager 的基礎執行依賴。",
      "latencyMon": "用於 ISR、DPC、hard pagefault 和驅動延遲診斷的輔助對照。",
      "usbIds": "提供 USB VID/PID 到廠商和裝置名稱的本地離線解析；僅在裝置詳情開啟時按需載入。",
      "pciIds": "提供 PCI VEN/DEV/SUBSYS 到廠商、裝置和子系統名稱的本地離線解析；僅在裝置詳情開啟時按需載入。"
    },
    "linkLabels": {
      "official": "官網",
      "github": "GitHub",
      "docs": "檔案",
      "license": "License",
      "nuget": "NuGet",
      "gpuOpen": "GPUOpen",
      "eula": "EULA",
      "runtime": "Runtime",
      "aspnet": "ASP.NET Core",
      "vite": "Vite",
      "solidPlugin": "Solid 外掛"
    }
  }
});
