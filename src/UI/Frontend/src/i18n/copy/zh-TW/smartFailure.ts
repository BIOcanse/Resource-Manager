import type { AppCopy } from "../zh/index.ts";

export const zhTwSmartFailureCopy: Pick<AppCopy, "smartFailure"> = {
  smartFailure: {
    title: {
      "cycle-failed": "本段排程失敗",
      "native-plan-failed": "原生排程計畫失敗"
    },
    hint: {
      "cycle-failed": "本段排程在執行過程中發生錯誤，未能完成。之後的排程段成功完成時，此提示會消失。錯誤的詳細資訊記錄在本機服務日誌中。",
      "native-plan-failed": "原生排程元件未能為本段產生排程計畫，本段未執行任何調整。計畫狀態說明了失敗原因。之後的排程段成功完成時，此提示會消失。"
    },
    nativePlanStatus: (status: string) => `計畫狀態：${status}`,
    nativePlanStatuses: {
      "invalid-argument": "參數無效",
      "abi-mismatch": "原生元件的介面版本不相符",
      unavailable: "原生元件無法使用",
      "no-data": "沒有可用資料",
      "buffer-too-small": "緩衝區容量不足",
      "stale-generation": "輸入資料已過期",
      "out-of-memory": "記憶體不足",
      "capacity-exceeded": "超出容量上限",
      "conflicting-facts": "輸入資料相互衝突",
      "feedback-mismatch": "執行回饋與計畫不一致",
      "recreate-required": "需要重新建立原生工作區"
    },
    exclusionTotal: (count: number) => `本段排除了 ${count} 項資料`,
    exclusionsHint: "排除的資料是本段因缺少、無效或相互不一致而未參與排程決策的資料。彙總依資料項目和原因分組計數，明細列出具體物件。",
    summaryLabel: "排除彙總",
    viewExclusions: "排除明細",
    detailTitle: "本段排除的資料",
    detailSummary: (listed: number, total: number) => `共排除 ${total} 項，以下列出 ${listed} 項。`,
    field: {
      processId: "處理程序 ID",
      processStartKey: "處理程序啟動識別碼",
      targetId: "排程物件識別碼",
      target: "排程物件"
    },
    noTarget: "不針對特定物件",
    facts: {
      inventory: "處理程序清單",
      identity: "處理程序身分",
      "base-score": "基礎分",
      attribution: "軟體歸屬",
      "runtime-state": "排程物件的執行狀態",
      "cpu-core-residency": "CPU 核心駐留",
      "cpu-score": "CPU 評分",
      "gpu-score": "GPU 評分",
      "compute-score": "算力評分",
      "memory-cleanup": "記憶體清理",
      "decision-target": "決策物件",
      "native-plan": "原生排程計畫",
      CpuUsage: "處理程序 CPU 使用率",
      MemoryUsage: "處理程序記憶體用量",
      GpuUsage: "處理程序 GPU 使用率",
      GpuDedicatedMemory: "處理程序獨立視訊記憶體用量",
      RuntimeState: "處理程序執行狀態"
    },
    reasons: {
      "dataset-not-current": "資料不是目前的取樣",
      "unidentified-live-process": "有無法識別的執行中處理程序",
      "inventory-overflow": "處理程序清單超出容量",
      "emitted-count-mismatch": "輸出筆數與處理程序清單不一致",
      "inventory-count-mismatch": "處理程序清單筆數與列舉數量不一致",
      "capture-count-mismatch": "擷取計數不一致",
      "invalid-or-duplicate-inventory-identity": "處理程序清單中有無效或重複的處理程序身分",
      "invalid-or-duplicate-process": "處理程序身分無效或重複",
      "row-missing-or-invalid": "資料缺少或無效",
      "gpu-row-missing-or-invalid": "GPU 資料缺少或無效",
      "compute-runtime-state-missing": "缺少執行狀態",
      "cpu-residency-not-covered": "CPU 駐留取樣未涵蓋此處理程序",
      "cpu-residency-invalid": "CPU 駐留資料無效",
      "target-process-count-not-one": "排程物件對應的處理程序數不是一個",
      "target-process-identity-missing": "排程物件缺少處理程序身分",
      "target-process-identity-mismatch": "排程物件的處理程序身分不一致",
      "duplicate-target-process-identity": "多個排程物件指向同一處理程序",
      "target-runtime-state-invalid": "排程物件的執行狀態無效",
      "cleanup-baseline-score-missing": "缺少清理基準評分",
      "cleanup-current-process-facts-missing": "缺少處理程序的目前資料",
      "cleanup-software-maximum-base-score-missing": "缺少軟體的最高基礎分",
      "ownership-software-attribution-mismatch": "變更所有權記錄與軟體歸屬不一致",
      "canonical-process-membership-mismatch": "處理程序與評分中的軟體成員不一致",
      "canonical-software-score-missing": "缺少軟體評分",
      "live-identity-without-decision-facts": "執行中的處理程序沒有決策資料",
      "canonical-process-not-projected": "處理程序評分未進入排程輸入",
      "canonical-software-membership-incomplete": "軟體評分的成員不完整"
    }
  }
};
