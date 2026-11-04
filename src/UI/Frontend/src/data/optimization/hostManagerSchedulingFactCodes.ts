/* Status and decision diagnostics share backend scheduling codes and their order. Unknown codes fail decoding; keep these lists and localized labels in sync. */

/** Backend scheduling fact codes. */
export const hostManagerSchedulingFacts = [
  "inventory", "identity", "attribution", "base-score", "cpu-usage", "memory-usage", "gpu-usage",
  "gpu-dedicated-memory", "gpu-private-memory-bytes", "gpu-shared-memory-bytes", "runtime-state",
  "cpu-core-residency", "compute-score", "cpu-score", "gpu-score", "decision-target", "memory-cleanup",
  "native-plan"
] as const;

export type HostManagerSchedulingFact = typeof hostManagerSchedulingFacts[number];

/** Non-Ok native plan states also serve as scheduling-fact exclusion reasons. */
export const hostManagerNativePlanStatuses = [
  "invalid-argument", "abi-mismatch", "unavailable", "no-data", "buffer-too-small",
  "stale-generation", "out-of-memory", "capacity-exceeded", "conflicting-facts",
  "feedback-mismatch", "recreate-required"
] as const;

export type HostManagerNativePlanStatus = typeof hostManagerNativePlanStatuses[number];

/** Exclusion reasons followed by native plan status codes. */
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
