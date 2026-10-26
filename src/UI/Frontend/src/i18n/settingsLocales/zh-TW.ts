import { createSettingsLocale } from "../settingsLocaleFactory.ts";
export default createSettingsLocale("zh-TW", {
  "updates": {
    "modeTitle": "更新方式",
    "modeDescription": "關閉時不做任何自動檢查；手動檢查和手動選擇版本在三檔下都可用。",
    "modeOptions": [{ id: "off", label: "關閉", description: "不自動檢查、下載或安裝。" }, { id: "notify", label: "只檢查並提示", description: "自動檢查，發現新版本時提示，不下載。" }, { id: "automatic", label: "自動下載並套用", description: "自動檢查、下載並校驗；退出介面後套用，也可點選立即套用。" }],
    "periodicTitle": "間隔檢查",
    "periodicDescription": "啟動檢查之後，按間隔再檢查。",
    "internetNote": "自動檢查會等到連網後進行；間隔只計連網的時間。",
    "offReason": "更新方式為「關閉」時不做自動檢查，間隔檢查和間隔不起作用；設定值會保留。",
    "intervalTitle": "檢查間隔（小時）",
    "intervalLabel": (minimum: number, maximum: number) => `間隔（小時，${minimum}–${maximum}）`,
    "inAppNoticeTitle": "介面內提示新版本",
    "inAppNoticeDescription": "介面執行時發現新版本，在介面內提示一次；同一版本不再提示。",
    "accessRequestNoticeTitle": "新存取請求的系統通知",
    "accessRequestNoticeDescription": "介面不可見時，收到新的待核准信任存取請求會顯示 Windows 通知。",
    "systemNoticeTitle": "Windows 系統通知",
    "systemNoticeDescription": "軟體不可見時發一次系統通知。此開關只儲存偏好，由桌面外殼（工作列通知區）執行。",
    "manualCheckNote": "手動檢查立即進行，不等連網；三檔下都可用。",
    "waitingForInternet": "正在等待連網；連網後立即檢查。",
    "availableVersion": "發現的新版本：",
    "nextCheck": "下次檢查：",
    "nextCheckAfterInternet": "連網後計時",
    "nextCheckOff": "未開啟",
    "connectedElapsed": "自上次檢查已連網：",
    "noticeTitle": "發現新版本",
    "noticeMessage": (version: string) => `資源管理器 ${version} 可以更新。`,
    "noticeReadyMessage": (version: string) => `資源管理器 ${version} 已下載並校驗，可以立即套用。`,
    "noticeOpenUpdates": "檢視更新與版本",
    "installedVersionTitle": "當前安裝版本",
    "unknownVersion": "無法識別",
    "checking": "檢查中…",
    "checkForUpdates": "檢查更新",
    "historyIncomplete": "版本歷史尚未完整讀取。",
    "historyStale": "正在顯示上次讀取的版本目錄；請聯網後重試。",
    "versionsTitle": "產品版本",
    "versionsDescription": "從新到舊排列；每個系列預設顯示最新一版。",
    "lastChecked": "上次檢查：",
    "selectedTarget": "已選擇目標版本：",
    "prepare": "準備所選更新",
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
      "installedVersionUnknown": "無法確認當前安裝版本，升級選擇已關閉。",
      "notNewer": "只能選擇高於當前安裝版本的發行版。"
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
    "automaticMemoryCleanupTitle": "緊急清理閾值",
    "automaticMemoryCleanupDescription": "背景清理依資源壓力分級；此處設定實體記憶體或認可額度剩餘低於多少時進入緊急清理。只在最佳化模式包含記憶體排程時生效。",
    "physicalMemoryAutomaticCleanupLabel": "實體記憶體剩餘 %",
    "virtualMemoryAutomaticCleanupLabel": "提交額度剩餘 %",
    "memoryOptimizationTargetTitle": "記憶體最佳化目標",
    "memoryOptimizationTargetDescription": "超過目標後慢慢釋放，不做緊急清理。",
    "physicalMemoryOptimizationTargetLabel": "實體記憶體目標使用率 %",
    "virtualMemoryOptimizationTargetLabel": "提交量目標使用率 %",
    "gpuOverflowTitle": "獨顯溢出閾值",
    "gpuOverflowDescription": "任一項達到閾值時，將背景低分處理程序從獨立顯示卡移到內建顯示晶片。僅在已啟用排程且內顯可用時生效。",
    "gpuOverflowUsageLabel": "獨顯使用率 %",
    "gpuOverflowDedicatedMemoryLabel": "獨顯專用視訊記憶體使用率 %",
    "realtimeActionBudgetTitle": "即時動作預算",
    "realtimeActionBudgetDescription": "每輪即時動作上限可按固定數量，或本輪識別的全部處理程序數的百分比（無條件進位）設定。切換模式會保留兩項數值。",
    "realtimeActionBudgetModeOptions": [{"id":"fixed","label":"固定數量"},{"id":"percent","label":"處理程序百分比"}],
    "realtimeActionBudgetCountLabel": "固定動作數量",
    "realtimeActionBudgetPercentLabel": "全部處理程序百分比 %",
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
    "refreshCadenceCustomLabel": "自定義 (ms)",
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
      "localSystem": {
        "label": "本機狀態",
        "description": "開機時間等低頻狀態"
      }
    }
  },
  "appearance": {
    "cacheDescription": "\u5916\u89C0\u3001\u91CD\u65B0\u6574\u7406\u9593\u9694\u8207\u6D6E\u7A97\u5916\u89C0\u5FEB\u53D6\u65BC\u6B64\u88DD\u7F6E\uFF1B\u6E05\u9664\u5FEB\u53D6\u5F8C\u6703\u6062\u5FA9\u9810\u8A2D\u503C\u3002",
    "resetCache": "\u91CD\u8A2D\u4ECB\u9762\u504F\u597D",
    "uiStyleTitle": "介面風格",
    "uiStyleDescription": "改變控制項的圓角、線條、陰影和字型，版面不變。每種風格有自己的配色。",
    "themeTitle": "配色",
    "themeDescription": "選擇目前風格提供的配色。跟隨系統會依 Windows 的淺色或深色設定切換。",
    "animationTitle": "動畫效果",
    "animationDescription": "選擇完整動畫、關閉動畫，或進一步簡化視覺以降低介面開銷。",
    "motionFeelTitle": "手感",
    "motionFeelDescription": "改變整個介面動畫的節奏和曲線，拖曳控制項的到位、回彈也一起變。動畫效果選無動畫時仍然不動。",
    "resourceBarFrameRateTitle": "資源條動畫幀率",
    "resourceBarFrameRateDescription": "資源條過渡動畫每秒繪製幾幀。跟隨螢幕依顯示器更新率繪製；自定義可限制幀數以降低開銷。只在正常動畫下生效。",
    "resourceBarFrameRateModeOptions": [
      {
        "id": "display",
        "label": "跟隨螢幕",
        "description": "每次螢幕更新都繪製一幀。"
      },
      {
        "id": "custom",
        "label": "自定義",
        "description": "每秒最多繪製設定的幀數（10–240）。"
      }
    ],
    "resourceBarFrameRateCustomLabel": "幀率 (FPS)",
    "barColorTitle": "條形圖配色",
    "barColorDescription": "按軟體型別使用固定顏色，或為每個軟體分配不同顏色。",
    "byteUnitTitle": "容量單位",
    "byteUnitDescription": "1 GiB = 1024 MiB，1 GB = 1000 MB。切換後所有容量數字一起變，不只是改標籤。",
    "readingSourceTitle": "讀數來源",
    "readingSourceDescription": "監視控制檯和裝置詳細資訊中，同一讀數有多個來源時如何列出。預設只列本機最準確的來源；顯示所有可用來源時，最準確的標為「首選」。排程一律使用最準確的來源，不受此處影響。",
    "readingSourceModeOptions": [
      {
        "id": "preferred",
        "label": "僅最準確",
        "description": "每個讀數只列出本機最準確的來源。"
      },
      {
        "id": "all",
        "label": "所有可用",
        "description": "列出每個讀數的所有可用來源，並標出最準確的。"
      }
    ],
    "fontSmoothingTitle": "文字平滑",
    "fontSmoothingDescription": "自動平衡文字清晰度和介面開銷，也可以固定渲染方式。",
    "languageTitle": "介面語言",
    "languageDescription": "選擇 Resource Manager 介面使用的語言。跟隨系統會使用 Windows 顯示語言。",
    "settingsLanguageTitle": "介面語言",
    "settingsLanguageDescription": "切換整個介面的語言，包括桌面外殼的托盤與視窗標題；跟隨系統會使用 Windows 顯示語言。",
    "languageSelectLabel": "語言",
    "motionFeelOptions": {
      "standard": "標準",
      "smooth": "平滑",
      "crisp": "俐落"
    },
    "uiStyleOptions": {
      "standard": "標準",
      "soft": "柔和",
      "outline": "線框",
      "terminal": "終端"
    },
    "themeOptions": {
      "system": "跟隨系統",
      "light": "淺色",
      "dark": "深色",
      "lowContrast": "低對比",
      "paper": "紙面",
      "graphite": "石墨",
      "blueprint": "藍圖",
      "phosphor": "磷綠",
      "amber": "琥珀",
      "ice": "冰藍"
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
  },
  "systemIntegration": {
    "crashRecoveryTitle": "當機後重新啟動",
    "crashRecoveryDescription": "背景服務當機時自動重新啟動。正常停止不會觸發重新啟動。",
    "crashRestartDelayTitle": "重新啟動前等待（秒）",
    "crashRestartCountTitle": "允許重新啟動次數",
    "crashCounterResetDescription": "連續 {days} 天未當機後重設計數。達到次數上限後，需手動啟動服務。",
    "crashCounterResetTitle": "計數重設週期（天）",
    "crashRecoveryActualTitle": "目前實際設定",
    "crashRecoveryUnavailable": "暫時無法確認自動復原設定。",
    "crashRecoveryApplied": "自動復原設定已套用。",
    "crashRecoverySkipped": "目前執行方式不套用自動復原設定。",
    "crashRecoveryFailed": "無法套用自動復原設定。",
    "crashRecoveryMismatch": "目前實際設定與所選設定不同。",
    "crashRecoveryNonCrashWarning": "目前設定也會在正常停止並傳回錯誤時重新啟動。",
    "crashRecoveryWriteFailed": "無法寫入自動復原設定。",
    "crashRecoveryReadFailed": "無法讀取自動復原設定。",
    "taskManagerTitle": "工作管理員快捷鍵替換",
    "autoStartTitle": "背景服務開機啟動",
    "autoStartDescription": "開機時自動啟動背景服務。",
    "loginStartTitle": "登入後進入系統匣",
    "loginStartDescription": "僅為目前使用者註冊登入啟動介面。背景服務另行設定。",
    "loginStartUnavailable": "無法讀取或儲存目前使用者的登入啟動設定。",
    "loginStartLoading": "正在讀取登入啟動設定…",
    "loginStartWriteFailed": "未能儲存登入啟動設定。開關顯示的是重新讀取到的實際狀態。",
    "loginStartRetry": "重試",
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
    "aiGatewayLoadFailed": "讀取本地 AI 金鑰失敗",
    "trustedAccess": {
      "title": "可信存取金鑰",
      "description": "管理通用存取金鑰與一次性授權請求。",
      "loading": "正在讀取存取金鑰…",
      "loadFailed": "無法讀取可信存取金鑰。",
      "actionFailed": "可信存取操作失敗。",
      "retry": "重試",
      "failureReasons": {
        "unauthorized": "本機服務不接受此介面的金鑰：預設金鑰可能已關閉、剛剛輪替或已失效。可以重試；如果預設金鑰已關閉，請以系統管理員身分重新啟用。",
        "scope-required": "目前的金鑰沒有「管理存取金鑰」範圍，無法執行此操作。",
        "default-scope-denied": "修改預設金鑰需要目前的金鑰具備全部預設範圍，或以系統管理員身分操作。"
      },
      "defaultTitle": "預設金鑰檔案",
      "defaultDescription": "本機原生殼透過受控橋接讀取預設金鑰，並隨請求附加。",
      "defaultEnabled": "已啟用",
      "defaultDisabled": "已關閉",
      "defaultKeyName": "預設金鑰",
      "defaultFilePath": "檔案位置",
      "defaultFileLocation": "<安裝目錄>\\UserData\\Security\\default-key",
      "defaultSecretLocalOnly": "預設金鑰只由本機原生殼讀取；此處不顯示金鑰內容。",
      "defaultAccessLossWarning": "關閉預設金鑰會立即撤銷它，並中斷依賴它的介面存取。若原生殼無法重新連線，請使用獨立金鑰或提升的管理員權限復原。輪替會使舊金鑰失效；原生殼會重新讀取預設金鑰檔並重新連線。",
      "defaultDisabledRecovery": "預設金鑰已關閉。此介面依靠預設金鑰存取本機服務，之後的請求會被拒絕。若要恢復，請以系統管理員身分重新啟用預設金鑰。",
      "enableDefaultElevated": "以系統管理員身分重新啟用預設金鑰",
      "disableDefault": "關閉並撤銷預設金鑰",
      "regenerateDefault": "重新產生預設金鑰",
      "confirmDisableTitle": "關閉並撤銷預設金鑰？",
      "confirmDisableMessage": "此介面依靠預設金鑰存取本機服務。關閉後預設金鑰立即失效，所有頁面都會失去存取，只能以系統管理員身分重新啟用預設金鑰來恢復。若在恢復前關閉介面，重新開啟後將無法在介面中恢復。",
      "confirmRegenerateTitle": "重新產生預設金鑰？",
      "confirmRegenerateMessage": "舊的預設金鑰立即失效。原生殼會重新讀取預設金鑰檔案並重新連線，在此之前介面的請求可能會被拒絕。",
      "confirmRotateTitle": (name: string) => `輪替「${name}」？`,
      "confirmRotateMessage": "舊金鑰立即失效，使用它的程式會被拒絕，直到換上新金鑰。新金鑰只顯示一次。",
      "confirmRevokeTitle": (name: string) => `撤銷「${name}」？`,
      "confirmRevokeMessage": "金鑰立即失效，使用它的程式會被拒絕。撤銷後無法恢復。",
      "createTitle": "建立獨立金鑰",
      "createDescription": "選擇金鑰範圍。新金鑰只會顯示一次，可個別輪替或撤銷。",
      "nameLabel": "名稱",
      "namePlaceholder": "例如：本機指令碼",
      "createScopesLabel": "授予範圍",
      "noScopesAvailable": "目前沒有可建立金鑰的範圍。",
      "create": "產生獨立金鑰",
      "working": "處理中…",
      "oneTimeTitle": "獨立金鑰已產生",
      "oneTimeDescription": "請立即複製並儲存，之後無法再次查看。",
      "oneTimeAnnouncement": "金鑰已產生，請立即複製保存。",
      "apiKeyLabel": "金鑰",
      "copy": "複製",
      "copied": "已複製",
      "copyFailed": "無法複製金鑰。",
      "dismissSecret": "隱藏一次性金鑰",
      "createdAt": "建立時間",
      "keysTitle": "獨立金鑰",
      "keysEmpty": "尚未建立獨立金鑰。",
      "revoke": "撤銷",
      "rotate": "輪替",
      "rotateKeyLabel": (name: string) => `輪替「${name}」`,
      "revokeKeyLabel": (name: string) => `撤銷「${name}」`,
      "requestsTitle": "一次性授權請求",
      "requestsDescription": "第三方程式提出請求後，會顯示其選擇的名稱與申請範圍。",
      "reviewRequests": "檢視授權請求",
      "requestsLoading": "正在等待授權請求…",
      "requestsUnavailable": "授權請求訂閱無法使用，暫時收不到新的請求。",
      "requestsLoadFailed": "無法讀取待處理的授權請求。",
      "requestsStale": "授權請求清單可能已過時，正在等待重新連線。",
      "requestsEmpty": "目前沒有待處理的請求。",
      "requestNamed": (name: string) => `請求名稱：${name}`,
      "requestCount": (count: number) => `待處理請求：${count} 個`,
      "expiresAt": "到期時間",
      "observedExecutable": "觀測到的程式路徑",
      "observedProcess": "PID",
      "submittedAt": "提交時間",
      "observedCallerIsInformational": "觀測到的路徑與 PID 僅供參考，不是授權身分。",
      "requestScopesLabel": "申請範圍",
      "requestUnavailableScopeHelp": "申請範圍中超出目前授權範圍的項目無法勾選；核准只會提交目前可授予且包含在請求中的範圍。",
      "approve": "核准所選範圍",
      "deny": "拒絕",
      "denyReasonLabel": "拒絕原因",
      "requestActionFailed": "無法提交授權決定。",
      "requestApproved": "已核准。金鑰已交付給請求程式。",
      "requestDenied": "已拒絕。",
      "requestDeniedWithReason": (reason: string) => `已拒絕（原因：${reason}）。`,
      "requestExpired": "請求已到期。",
      "requestOutcome": (name: string, result: string) => `${name}：${result}`,
      "denialReasons": {
        "not-approved": "不核准",
        "unsupported": "不支援",
        "duplicate": "重複請求",
        "not-needed": "不需要"
      },
      "scopeLabels": {
        "read": "讀取介面",
        "metrics.read": "讀取指標",
        "processes.read": "讀取處理程序",
        "session": "工作階段操作",
        "adapters.register.self": "登記目前使用者的介接器",
        "desktop.publish": "發布桌面觀測資料",
        "gpu.resolve": "解析 GPU 放置目標",
        "gpu.report": "回報 GPU 啟動結果",
        "keys.manage": "管理存取金鑰",
        "machine.write": "修改電腦設定",
        "injection.configure": "設定啟動注入"
      },
      "unknownScope": "未知範圍（此版本無法說明，無法在此授予）"
    }
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
      "nvidiaTelemetry": "NVIDIA GPU 遙測",
      "nvidiaExtension": "NVIDIA GPU 擴充套件介面",
      "amdTelemetry": "AMD GPU/iGPU 遙測",
      "signedHardwareAccess": "簽署硬體存取",
      "hardwareBridge": "通用硬體感測橋",
      "externalReference": "外部遙測參考 / 可選輔助",
      "deviceIdDatabase": "硬體 ID 名稱資料庫"
    },
    "linkLabels": {
      "official": "官方網站",
      "github": "GitHub",
      "docs": "文件",
      "nuget": "NuGet",
      "npm": "npm",
      "license": "授權條款"
    },
    "sourceLabel": "來源",
    "dependencyColumns": {
      "name": "名稱",
      "version": "版本",
      "license": "授權條款",
      "source": "來源"
    }
  }
});
