import type {
  HostManagerCycleFailureKind,
  HostManagerNativePlanStatus,
  HostManagerSchedulingExclusionReason,
  HostManagerSchedulingFact
} from "../../../types.ts";

// 优化页 → 调度段失败的提示（GET /api/optimization/smart/status 与其推送的 failure 和事实排除）。
// 失败类型、原生计划状态码、事实和排除原因都是封闭集合（码表见 data/optimization/hostManagerSchedulingFactCodes.ts），
// 解码器拒绝未知值，这里逐个给出文字；缺一个码的文字就是类型错误。
// 原生计划的排除原因就是计划状态码：reasons 展开同一份 nativePlanStatuses，不另写一遍。
const nativePlanStatuses: Record<HostManagerNativePlanStatus, string> = {
  "invalid-argument": "参数无效",
  "abi-mismatch": "原生组件的接口版本不匹配",
  unavailable: "原生组件不可用",
  "no-data": "没有可用数据",
  "buffer-too-small": "缓冲区容量不足",
  "stale-generation": "输入数据已过期",
  "out-of-memory": "内存不足",
  "capacity-exceeded": "超出容量上限",
  "conflicting-facts": "输入数据相互冲突",
  "feedback-mismatch": "执行反馈与计划不一致",
  "recreate-required": "需要重新建立原生工作区"
};

export const zhSmartFailureCopy = {
  smartFailure: {
    title: {
      "cycle-failed": "本段调度失败",
      "native-plan-failed": "原生调度计划失败"
    } as Record<HostManagerCycleFailureKind, string>,
    hint: {
      "cycle-failed": "本段调度在执行过程中出错，未能完成。之后的调度段成功完成时，此提示消失。错误的详细信息记录在本地服务日志中。",
      "native-plan-failed": "原生调度组件未能为本段生成调度计划，本段未执行任何调整。计划状态说明了失败原因。之后的调度段成功完成时，此提示消失。"
    } as Record<HostManagerCycleFailureKind, string>,
    nativePlanStatus: (status: string) => `计划状态：${status}`,
    nativePlanStatuses,
    exclusionTotal: (count: number) => `本段排除了 ${count} 项数据`,
    exclusionsHint: "排除的数据是本段因缺失、无效或相互不一致而未参与调度决策的数据。汇总按数据项和原因分组计数，明细列出具体对象。",
    summaryLabel: "排除汇总",
    viewExclusions: "排除明细",
    detailTitle: "本段排除的数据",
    detailSummary: (listed: number, total: number) => `共排除 ${total} 项，以下列出 ${listed} 项。`,
    field: {
      processId: "进程 ID",
      processStartKey: "进程启动标识",
      targetId: "调度对象标识",
      target: "调度对象",
      adapterKey: "GPU 适配器标识"
    },
    noTarget: "不针对具体对象",
    facts: {
      inventory: "进程清单",
      identity: "进程身份",
      attribution: "软件归属",
      "base-score": "基础分",
      "cpu-usage": "进程 CPU 使用率",
      "memory-usage": "进程内存用量",
      "gpu-usage": "进程 GPU 使用率",
      "gpu-dedicated-memory": "进程独立显存用量",
      "gpu-private-memory-bytes": "进程私有 GPU 内存驻留量",
      "gpu-shared-memory-bytes": "进程共享 GPU 内存驻留量",
      "runtime-state": "进程运行状态",
      "cpu-core-residency": "CPU 核心驻留",
      "compute-score": "算力评分",
      "cpu-score": "CPU 评分",
      "gpu-score": "GPU 评分",
      "decision-target": "决策对象",
      "memory-cleanup": "内存清理",
      "native-plan": "原生调度计划"
    } satisfies Record<HostManagerSchedulingFact, string>,
    reasons: {
      "dataset-not-current": "数据不是当前采样",
      "unidentified-live-process": "存在无法识别的运行中进程",
      "inventory-overflow": "进程清单超出容量",
      "emitted-count-mismatch": "输出条数与进程清单不一致",
      "inventory-count-mismatch": "进程清单条数与枚举数量不一致",
      "capture-count-mismatch": "采集计数不一致",
      "invalid-or-duplicate-inventory-identity": "进程清单中有无效或重复的进程身份",
      "invalid-or-duplicate-process": "进程身份无效或重复",
      "row-missing-or-invalid": "数据缺失或无效",
      "gpu-row-missing-or-invalid": "GPU 数据缺失或无效",
      "compute-runtime-state-missing": "缺少运行状态",
      "cpu-residency-not-covered": "CPU 驻留采样未覆盖该进程",
      "cpu-residency-invalid": "CPU 驻留数据无效",
      "target-process-count-not-one": "调度对象对应的进程数不是一个",
      "target-process-identity-missing": "调度对象缺少进程身份",
      "target-process-identity-mismatch": "调度对象的进程身份不一致",
      "duplicate-target-process-identity": "多个调度对象指向同一进程",
      "target-runtime-state-invalid": "调度对象的运行状态无效",
      "cleanup-baseline-score-missing": "缺少清理基准评分",
      "cleanup-current-process-facts-missing": "缺少进程的当前数据",
      "cleanup-software-maximum-base-score-missing": "缺少软件的最高基础分",
      "ownership-software-attribution-mismatch": "改动所有权记录与软件归属不一致",
      "canonical-process-membership-mismatch": "进程与评分中的软件成员不一致",
      "canonical-software-score-missing": "缺少软件评分",
      "live-identity-without-decision-facts": "运行中的进程没有决策数据",
      "canonical-process-not-projected": "进程评分未进入调度输入",
      "canonical-software-membership-incomplete": "软件评分的成员不完整",
      ...nativePlanStatuses
    } satisfies Record<HostManagerSchedulingExclusionReason, string>
  }
};
