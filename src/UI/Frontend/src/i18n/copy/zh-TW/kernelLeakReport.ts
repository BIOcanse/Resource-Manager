import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../zh/index.ts";

export const zhTwKernelLeakReportCopy: Pick<AppCopy, "kernelLeakReport"> = {
  kernelLeakReport: {
    tab: "核心洩漏報告",
    intro: "本機服務在執行期間持續觀察各處理程序持有的核心資源（目前為處理程序控制代碼），列出持續增長、疑似無界增長的處理程序及其判定依據。判定結果僅表示疑似，是否處理請依據判定依據決定。",
    loading: "正在讀取核心洩漏報告…",
    readFailed: "核心洩漏報告讀取失敗",
    capturedAt: "擷取時間",
    suspectedItems: "疑似項目",
    notObservedTitle: "未在觀察核心洩漏",
    notObservedDetail: "核心洩漏觀察目前沒有執行：可能已關閉、觀察計畫尚未發布，或已停止。執行之前不會取樣，也不會產生報告。",
    observingTitle: "正在觀察",
    observingDetail: "本輪觀察的時長與樣本數均達到要求後開始判定。",
    emptyTitle: "未發現疑似核心洩漏",
    emptyDetail: "本輪觀察的時長與樣本數已達到要求，未發現持續增長的核心資源。",
    unavailableTitle: "本輪觀察失敗",
    failure: {
      SourceUnavailable: "無法讀取處理程序的資源資料。",
      CapacityExceeded: "觀察的處理程序數量超過容量上限，本輪觀察已停止。",
      InvalidObservation: "取樣資料無效，本輪觀察已停止。"
    },
    observation: {
      title: "本輪觀察",
      startedAt: "本輪開始時間",
      elapsed: "觀察時長",
      samples: "樣本數",
      notStarted: "尚未取樣",
      reachedOfRequired: (reached: string, required: string) => `${reached}（需要 ${required}）`
    },
    previousRun: {
      title: "上一輪觀察失敗",
      reason: "原因",
      startedAt: "該輪開始時間",
      failedAt: "失敗時間"
    },
    restartPolicy: {
      NextInterval: "本機服務會在下一個取樣間隔自動開始新一輪觀察。"
    },
    unavailableHolders: (count: number) => cjkText`有${count}個處理程序的資源資料無法讀取，未納入本次判定。`,
    resourceKind: {
      ProcessHandles: "處理程序控制代碼"
    },
    holder: {
      processId: "PID",
      productName: "產品名稱",
      services: "服務",
      imagePath: "映像路徑",
      noProductName: "未提供",
      noServices: "無對應服務",
      unavailable: "無法讀取",
      processEnded: "處理程序已結束"
    },
    evidence: {
      currentValue: "目前值",
      growth: "觀察期間增長",
      growthPerSecond: "增長速率",
      observationTime: "觀察時長",
      sampleCount: "樣本數"
    },
    ratePerSecond: (value: string) => `${value}/秒`,
    actions: {
      title: "處理操作",
      name: {
        RestartHolder: "重新啟動持有程式",
        ForceReleaseHandles: "強制釋放控制代碼"
      },
      riskLevel: {
        Warning: "警告",
        High: "高"
      },
      riskText: (level: string, risk: string) => `風險：${level}。${risk}`,
      riskLabel: (name: string) => cjkText`${name}的風險`
    }
  }
};
