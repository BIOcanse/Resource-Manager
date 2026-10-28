import { cjkText } from "../../cjkSpacing.ts";

// 详细信息 → 内核泄漏报告（docs/kernel_leak_report_feature_2026-10-27.md，合同 v2）。只描述本地服务发来的内容；
// 处理操作的风险说明和不可用的原因是后端消息码，措辞在 backendMessages.ts 的 kernelLeak；这里只有操作名称和风险等级的名称。
export const zhKernelLeakReportCopy = {
  kernelLeakReport: {
    tab: "内核泄漏报告",
    intro: "本地服务在运行期间持续观察各进程持有的内核资源（目前为进程句柄），列出持续增长、疑似无界增长的进程及其判定依据。判定结果仅表示疑似，是否处理请根据判定依据决定。",
    loading: "正在读取内核泄漏报告…",
    readFailed: "内核泄漏报告读取失败",
    capturedAt: "采集时间",
    suspectedItems: "疑似项",
    notObservedTitle: "未在观察内核泄漏",
    notObservedDetail: "内核泄漏观察目前没有运行：可能已关闭、观察计划尚未发布，或已停止。运行之前不进行采样，也不生成报告。",
    observingTitle: "正在观察",
    observingDetail: "本轮观察的时长和样本数均达到要求后开始判定。",
    emptyTitle: "未发现疑似内核泄漏",
    emptyDetail: "本轮观察的时长和样本数已达到要求，未发现持续增长的内核资源。",
    unavailableTitle: "本轮观察失败",
    failure: {
      SourceUnavailable: "无法读取进程的资源数据。",
      CapacityExceeded: "观察的进程数量超过容量上限，本轮观察已停止。",
      InvalidObservation: "采样数据无效，本轮观察已停止。"
    },
    observation: {
      title: "本轮观察",
      startedAt: "本轮开始时间",
      elapsed: "观察时长",
      samples: "样本数",
      notStarted: "尚未采样",
      reachedOfRequired: (reached: string, required: string) => `${reached}（需要 ${required}）`
    },
    previousRun: {
      title: "上一轮观察失败",
      reason: "原因",
      startedAt: "该轮开始时间",
      failedAt: "失败时间"
    },
    restartPolicy: {
      NextInterval: "本地服务会在下一个采样间隔自动开始新一轮观察。"
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
      currentValue: "当前值",
      growth: "观察期间增长",
      growthPerSecond: "增长速率",
      observationTime: "观察时长",
      sampleCount: "样本数"
    },
    ratePerSecond: (value: string) => `${value}/秒`,
    actions: {
      title: "处理操作",
      name: {
        RestartHolder: "重启持有程序",
        ForceReleaseHandles: "强制释放句柄"
      },
      riskLevel: {
        Warning: "警告",
        High: "高"
      },
      riskText: (level: string, risk: string) => `风险：${level}。${risk}`,
      riskLabel: (name: string) => cjkText`${name}的风险`
    }
  }
};
