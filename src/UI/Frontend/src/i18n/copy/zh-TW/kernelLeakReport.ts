import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../zh/index.ts";

export const zhTwKernelLeakReportCopy: Pick<AppCopy, "kernelLeakReport"> = {
  kernelLeakReport: {
    tab: "核心洩漏報告",
    intro: "列出核心資源（目前為處理程序控制代碼）持續增長、疑似無界增長的處理程序及其判定依據。判定結果僅表示疑似，是否處理請依據判定依據決定。僅在此頁面顯示期間進行觀察，離開後再次開啟會重新開始觀察。",
    loading: "正在讀取核心洩漏報告…",
    readFailed: "核心洩漏報告讀取失敗",
    capturedAt: "擷取時間",
    suspectedItems: "疑似項目",
    emptyTitle: "未發現疑似核心洩漏",
    emptyDetail: "觀察時間尚不足以判定時同樣顯示此結果。發現疑似項目後會顯示於此處。",
    notObservedTitle: "尚未取得首次取樣",
    unavailableTitle: "核心洩漏報告無法使用",
    failure: {
      NotObserved: "觀察已開始，首次取樣完成後顯示結果。",
      SourceUnavailable: "無法讀取處理程序的資源資料。",
      CapacityExceeded: "觀察的處理程序數量超過容量上限，本次觀察已停止。",
      InvalidObservation: "取樣資料無效，本次觀察已停止。"
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
      currentValue: "目前數量",
      growth: "觀察期間增長",
      growthPerSecond: "增長速率",
      observationTime: "觀察時長",
      sampleCount: "樣本數"
    },
    ratePerSecond: (value: string) => `${value}/秒`,
    actionsPending: "本機服務尚未提供此項目的處理操作。"
  }
};
