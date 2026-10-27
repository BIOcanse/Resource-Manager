import { cjkText } from "../../cjkSpacing.ts";

// 详细信息 → 内核泄漏报告（docs/kernel_leak_report_feature_2026-10-27.md）。只描述本地服务发来的内容；
// 处理动作的名称、风险说明和是否可用由本地服务提供（第 3 期），这里不写。
export const zhKernelLeakReportCopy = {
  kernelLeakReport: {
    tab: "内核泄漏报告",
    intro: "列出内核资源（目前为进程句柄）持续增长、疑似无界增长的进程及其判定依据。判定结果仅表示疑似，是否处理请根据判定依据决定。仅在此页面显示期间进行观察，离开后再次打开会重新开始观察。",
    loading: "正在读取内核泄漏报告…",
    readFailed: "内核泄漏报告读取失败",
    capturedAt: "采集时间",
    suspectedItems: "疑似项",
    emptyTitle: "未发现疑似内核泄漏",
    emptyDetail: "观察时间尚不足以判定时同样显示此结果。发现疑似项后会显示在此处。",
    notObservedTitle: "尚未取得首次采样",
    unavailableTitle: "内核泄漏报告不可用",
    failure: {
      NotObserved: "观察已开始，首次采样完成后显示结果。",
      SourceUnavailable: "无法读取进程的资源数据。",
      CapacityExceeded: "观察的进程数量超过容量上限，本次观察已停止。",
      InvalidObservation: "采样数据无效，本次观察已停止。"
    },
    unavailableHolders: (count: number) => cjkText`有${count}个进程的资源数据无法读取，未纳入本次判定。`,
    resourceKind: {
      ProcessHandles: "进程句柄"
    },
    holder: {
      processId: "PID",
      productName: "产品名称",
      services: "服务",
      imagePath: "映像路径",
      noProductName: "未提供",
      noServices: "无对应服务",
      unavailable: "无法读取",
      processEnded: "进程已结束"
    },
    evidence: {
      currentValue: "当前数量",
      growth: "观察期间增长",
      growthPerSecond: "增长速率",
      observationTime: "观察时长",
      sampleCount: "样本数"
    },
    ratePerSecond: (value: string) => `${value}/秒`,
    actionsPending: "本地服务尚未提供此项的处理操作。"
  }
};
