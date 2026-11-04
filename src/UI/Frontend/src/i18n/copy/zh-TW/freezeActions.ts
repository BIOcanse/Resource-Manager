import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../zh/index.ts";

export const zhTwFreezeActionsCopy: Pick<AppCopy, "deviceActions" | "crashDumpKeys" | "cpuMonopoly"> = {
  deviceActions: {
    restart: {
      label: "重新啟動裝置",
      busy: "正在重新啟動",
      hint: "停用並重新啟用這筆報告指向的裝置，可以讓卡住的驅動程式重新開始運作。每次都需要你確認，軟體不會自動執行。",
      confirmTitle: "要重新啟動這個裝置嗎？",
      confirmMessage: (target: string) => cjkText`將停用並重新啟用「${target}」對應的裝置。`,
      confirmDetails: [
        "只在你確認後執行這一次，不會自動重複。",
        "需要系統管理員權限；結果會寫入詳細資訊裡的裝置動作記錄。"
      ],
      risks: {
        DeviceRestartNetworkInterrupted: "如果是網路介面卡，網路連線會暫時中斷。",
        DeviceRestartDisplayStateLost: "如果是顯示卡，畫面會短暫變黑，視窗位置和顯示設定可能遺失。",
        DeviceRestartMayRequireReboot: "部分裝置要重新啟動電腦才能恢復。"
      },
      unknownRisk: (code: string) => cjkText`其他風險（${code}）。`,
      confirm: "重新啟動裝置",
      doneTitle: "裝置已重新啟動",
      refusedTitle: "裝置沒有重新啟動",
      failed: "重新啟動裝置失敗。",
      reason: (text: string) => cjkText`結論：${text}`,
      deviceResult: (text: string) => cjkText`裝置結果：${text}`,
      win32Detail: (code: string) => cjkText`系統錯誤碼：${code}`,
      recorded: (sequence: string) => cjkText`已寫入裝置動作記錄（序號 ${sequence}）。`,
      notRecorded: "這次要求沒有寫入記錄。"
    },
    reasons: {
      completed: "已完成",
      "confirmation-required": "需要確認",
      "report-not-found": "找不到這筆報告",
      "unsupported-report": "這類報告不支援重新啟動裝置",
      "device-evidence-unavailable": "報告裡沒有能確定裝置的證據",
      "invalid-settings": "設定無效",
      "record-capacity-reached": "裝置動作記錄已滿，沒有執行",
      "persistence-failed": "記錄寫入失敗，沒有執行",
      "device-resolution-failed": "無法確定要重新啟動的裝置",
      "device-restart-failed": "重新啟動裝置失敗",
      "notification-failed": "裝置已處理，但通知傳送失敗"
    },
    results: {
      success: "成功",
      "confirmation-required": "需要確認",
      "invalid-instance-id": "裝置執行個體識別碼無效",
      "administrator-required": "需要系統管理員權限",
      "device-not-found": "找不到裝置",
      "device-unavailable": "裝置目前無法使用",
      "disable-failed": "停用裝置失敗",
      "enable-failed": "重新啟用裝置失敗",
      "reboot-required": "需要重新啟動電腦",
      "verification-failed": "重新啟動後檢查未通過",
      "driver-not-found": "找不到驅動程式",
      "ambiguous-device": "符合多個裝置，沒有執行",
      "invalid-module": "驅動程式模組無效",
      "enumeration-failed": "列舉裝置失敗",
      "buffer-too-small": "系統緩衝區不足",
      "unsupported-platform": "目前的系統不支援",
      interrupted: "進行中被中斷，結果無法確定"
    },
    unknownCode: (code: string) => cjkText`代碼 ${code}`,
    records: {
      tab: "裝置動作記錄",
      intro: "在報告上確認「重新啟動裝置」後，每次操作的結果記在這裡。記錄會持續保存，不會自動刪除；進行中的記錄在操作結束後轉為歷史。",
      loading: "正在讀取裝置動作記錄…",
      readFailed: "裝置動作記錄讀取失敗",
      empty: "還沒有裝置動作記錄。",
      loadMore: "載入更早的記錄",
      loadingMore: "正在載入",
      refresh: "重新整理",
      shown: (count: string) => cjkText`已顯示${count}筆記錄。`,
      states: { active: "進行中", history: "歷史" },
      device: "裝置執行個體",
      report: "報告項目",
      time: "時間",
      result: "結果",
      detail: "系統錯誤碼",
      sequence: "序號"
    }
  },
  crashDumpKeys: {
    title: "藍色畫面傾印快速鍵",
    description: "開啟後，按住右 Ctrl 鍵並連按兩次 Scroll Lock 鍵，系統會立即出現藍色畫面並寫下記憶體傾印。電腦當住、無法正常操作時，可以用它留下現場供事後分析。預設關閉。",
    details: [
      "只有你按下這組按鍵時才會觸發，平時沒有任何作用。",
      "觸發後電腦會立即重新啟動，未儲存的工作會遺失。",
      "變更在重新啟動電腦後生效，寫入時需要系統管理員權限；關閉後會還原開啟前的系統設定。"
    ],
    notProvided: "後端沒有提供這項設定。",
    statusTitle: "系統中的狀態",
    states: {
      takesEffectAfterRestart: "已寫入系統，重新啟動電腦後生效。",
      off: "未開啟。",
      restorePending: "正在還原開啟前的系統設定，重新啟動電腦後完成。",
      configurationMismatch: "系統裡的按鍵設定和這裡的選擇不一致。",
      failed: "寫入系統設定失敗。"
    },
    combinations: {
      rightCtrlScrollLockTwice: "按鍵：右 Ctrl + 兩次 Scroll Lock",
      custom: "按鍵：其他程式或手動設定的自訂按鍵"
    },
    kernelDumpMissing: "系統沒有設定核心記憶體傾印，觸發後可能不會寫下傾印。",
    reason: (text: string) => cjkText`原因：${text}`,
    win32Detail: (code: string) => cjkText`系統錯誤碼：${code}`,
    reasons: {
      "invalid-crash-dump-keys": "按鍵設定無效",
      "crash-dump-keys-read-failed": "讀取系統設定失敗",
      "crash-dump-keys-write-failed": "寫入系統設定失敗",
      "crash-dump-keys-verify-failed": "寫入後檢查未通過",
      "crash-dump-keys-restore-pending": "開啟前的系統設定還沒有還原完成",
      "invalid-crash-dump-key-snapshot": "儲存的原始設定無效",
      "crash-dump-keys-access-denied": "需要系統管理員權限",
      "crash-dump-key-value-too-large": "系統中的原始值過大，無法儲存",
      "crash-dump-keys-configuration-mismatch": "系統裡的設定被其他程式變更過",
      "crash-dump-keys-bridge-failed": "本機服務無法處理這項設定"
    }
  },
  cpuMonopoly: {
    intro: "一個處理程序以高或即時優先順序持續占滿 CPU 時，系統可能當住、沒有回應。開啟後，這樣的處理程序會被調回正常優先順序：只調整優先順序，不結束、不暫停，也不會再改回去。主機記憶體保護的處理程序白名單同樣適用。預設關閉。",
    notProvided: "後端沒有提供 CPU 獨佔保護的設定。",
    unsavedNote: "有未儲存的變更，儲存後生效。",
    rejected: (reason: string) => cjkText`設定未被接受：${reason}`,
    enabledTitle: "啟用 CPU 獨佔保護",
    enabledDescription: "開啟後在背景檢查各處理程序的 CPU 使用率和優先順序；關閉時不檢查。",
    thresholdTitle: "使用率閾值",
    thresholdDescription: "以高或即時優先順序執行的處理程序，CPU 使用率（占全部處理器的百分比）達到這個值才開始計時。",
    thresholdLabel: "百分比",
    durationTitle: "持續時間",
    durationDescription: "使用率連續達到閾值這麼久，才把它調回正常優先順序。",
    durationLabel: "秒",
    intervalTitle: "檢查間隔",
    intervalDescription: "多久讀取一次各處理程序的 CPU 使用率。",
    intervalLabel: "秒",
    whitelistNote: "白名單在「主機記憶體保護」分區中編輯。",
    statusTitle: "目前狀態",
    statusLoading: "正在讀取 CPU 獨佔保護狀態…",
    statusUnavailable: "CPU 獨佔保護狀態讀取失敗",
    statusUnavailableDetail: "設定仍可編輯和儲存；狀態恢復後在這裡顯示。",
    stateActive: "正在檢查",
    stateInactive: (reason: string) => cjkText`未在檢查：${reason}`,
    inactiveReasons: {
      disabled: "未開啟",
      "not-running": "保護還沒有啟動",
      "process-data-unavailable": "讀不到處理程序資料",
      "process-exit-proof-unavailable": "無法確認處理程序是否已結束",
      "process-capacity": "處理程序數超過可追蹤的上限",
      stopped: "已停止"
    },
    currentAction: "正在生效的處理",
    exclusionsTitle: "排除的處理程序",
    exclusionsEmpty: "目前沒有排除的處理程序。",
    exclusionReasons: {
      "resource-manager": "本軟體自己的處理程序",
      "resource-manager-process": "本軟體自己的處理程序",
      "protected-process": "在白名單中",
      "critical-process": "系統關鍵處理程序",
      "unsupported-platform": "目前的系統不支援",
      "identity-changed": "處理程序已變化",
      "cannot-open": "無法存取這個處理程序",
      "criticality-unavailable": "無法確認是否為系統關鍵處理程序"
    },
    process: (name: string, processId: number) => cjkText`${name}（PID ${processId}）`,
    percent: (value: string) => `${value}%`,
    seconds: (value: string) => cjkText`${value} 秒`,
    priorities: { High: "高", RealTime: "即時" },
    records: {
      tab: "CPU 獨佔保護記錄",
      intro: "CPU 獨佔保護每處理一個處理程序記一筆。調回正常優先順序後，處理程序結束或你另行設定了它的優先順序時，記錄轉為歷史；處理失敗的直接是歷史。記錄會持續保存，不會自動刪除。",
      loading: "正在讀取 CPU 獨佔保護記錄…",
      unavailable: "CPU 獨佔保護記錄讀取失敗",
      empty: "還沒有處理記錄。",
      count: (count: string, capacity: string) => cjkText`共${count}筆記錄，最多保存${capacity}筆。`,
      unrecorded: (count: number) => cjkText`記錄已滿，之後有${count}次處理沒有記下；清除歷史後恢復記錄。`,
      outcomes: { normalized: "已調回正常優先順序", failed: "處理失敗" },
      states: { active: "仍在生效", history: "歷史" },
      time: "時間",
      cpu: "CPU 使用率",
      previousPriority: "原優先順序",
      threshold: "當時的閾值",
      duration: "當時的持續時間",
      reason: "原因",
      detail: "詳細資訊",
      endedAt: "結束時間",
      endReason: "結束原因"
    },
    actionReasons: {
      "process-exited": "處理程序已結束",
      "user-priority-set": "優先順序已被另行設定",
      "write-failed": "寫入優先順序失敗",
      "identity-changed": "處理程序已變化",
      "priority-changed": "優先順序已被其他程式變更",
      "priority-unavailable": "讀不到處理程序的優先順序",
      "priority-write-failed": "寫入優先順序失敗",
      "priority-readback-unavailable": "寫入後無法讀回優先順序",
      "priority-readback-mismatch": "寫入後讀回的優先順序不一致"
    },
    notice: {
      title: "CPU 獨佔保護",
      normalized: (target: string) => cjkText`已把${target}調回正常優先順序。`,
      failed: (target: string, reason: string) => cjkText`未能調整${target}：${reason}`,
      openRecords: "檢視記錄"
    }
  }
};
