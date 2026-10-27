/*
 * Smart 调度事实排除的封闭码表（状态推送和决策诊断用同一套码）。
 * 逐项等于后端 src/Core/Domain/ResourceBreakdown/SchedulingFactExclusionCodes.cs 的
 * SchedulingFactCode 与 SchedulingFactExclusionReason（JsonStringEnumMemberName 的短横线写法），顺序也相同。
 * 后端增删码时同时改这里和九种语言的 smartFailure 文字；解码器拒绝表外的码。
 */

/** SchedulingFactCode。 */
export const hostManagerSchedulingFacts = [
  "inventory", "identity", "attribution", "base-score", "cpu-usage", "memory-usage", "gpu-usage",
  "gpu-dedicated-memory", "gpu-private-memory-bytes", "gpu-shared-memory-bytes", "runtime-state",
  "cpu-core-residency", "compute-score", "cpu-score", "gpu-score", "decision-target", "memory-cleanup",
  "native-plan"
] as const;

export type HostManagerSchedulingFact = typeof hostManagerSchedulingFacts[number];

/**
 * 后端 NativeSmartCoordinatorStatus 除 Ok 外的成员（NativePlanStatusCode）：失败段的 nativePlanStatus，
 * 也是 SchedulingFactExclusionReason 的最后十一项（原生计划失败时的排除原因）。
 */
export const hostManagerNativePlanStatuses = [
  "invalid-argument", "abi-mismatch", "unavailable", "no-data", "buffer-too-small",
  "stale-generation", "out-of-memory", "capacity-exceeded", "conflicting-facts",
  "feedback-mismatch", "recreate-required"
] as const;

export type HostManagerNativePlanStatus = typeof hostManagerNativePlanStatuses[number];

/** SchedulingFactExclusionReason：前二十七项之后是原生计划状态码。 */
export const hostManagerSchedulingExclusionReasons = [
  "unidentified-live-process", "dataset-not-current", "inventory-overflow", "emitted-count-mismatch",
  "inventory-count-mismatch", "capture-count-mismatch", "invalid-or-duplicate-inventory-identity",
  "invalid-or-duplicate-process", "row-missing-or-invalid", "gpu-row-missing-or-invalid",
  "target-process-count-not-one", "target-process-identity-missing", "target-process-identity-mismatch",
  "duplicate-target-process-identity", "target-runtime-state-invalid", "compute-runtime-state-missing",
  "cpu-residency-not-covered", "cpu-residency-invalid", "ownership-software-attribution-mismatch",
  "canonical-process-membership-mismatch", "canonical-software-score-missing",
  "live-identity-without-decision-facts", "canonical-process-not-projected",
  "canonical-software-membership-incomplete", "cleanup-baseline-score-missing",
  "cleanup-current-process-facts-missing", "cleanup-software-maximum-base-score-missing",
  ...hostManagerNativePlanStatuses
] as const;

export type HostManagerSchedulingExclusionReason = typeof hostManagerSchedulingExclusionReasons[number];
