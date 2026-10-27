import { plural } from "../../plural.ts";
import type { AppCopy } from "../zh/index.ts";

export const enSmartFailureCopy: Pick<AppCopy, "smartFailure"> = {
  smartFailure: {
    title: {
      "cycle-failed": "This scheduling segment failed",
      "native-plan-failed": "The native scheduling plan failed"
    },
    hint: {
      "cycle-failed": "This scheduling segment encountered an error and did not complete. This notice disappears once a later segment completes successfully. Error details are recorded in the local service log.",
      "native-plan-failed": "The native scheduling component could not produce a plan for this segment, so no adjustments were made in it. The plan status states the cause. This notice disappears once a later segment completes successfully."
    },
    nativePlanStatus: (status: string) => `Plan status: ${status}`,
    nativePlanStatuses: {
      "invalid-argument": "Invalid argument",
      "abi-mismatch": "Native component interface version mismatch",
      unavailable: "Native component unavailable",
      "no-data": "No data available",
      "buffer-too-small": "Insufficient buffer capacity",
      "stale-generation": "Input data is out of date",
      "out-of-memory": "Insufficient memory",
      "capacity-exceeded": "Capacity limit exceeded",
      "conflicting-facts": "Input data is contradictory",
      "feedback-mismatch": "Execution feedback does not match the plan",
      "recreate-required": "The native workspace must be recreated"
    },
    exclusionTotal: (count: number) => plural("en-US", count, {
      one: "{count} data item was excluded from this segment",
      other: "{count} data items were excluded from this segment"
    }),
    exclusionsHint: "Excluded data is data that did not take part in this segment's scheduling decisions because it was missing, invalid or inconsistent. The summary counts exclusions by data item and reason; the details list the objects concerned.",
    summaryLabel: "Exclusion summary",
    viewExclusions: "Exclusion details",
    detailTitle: "Data excluded from this segment",
    detailSummary: (listed: number, total: number) => `Excluded in total: ${total}; listed below: ${listed}.`,
    field: {
      processId: "Process ID",
      processStartKey: "Process start key",
      targetId: "Scheduling target ID",
      target: "Scheduling target"
    },
    noTarget: "Not specific to an object",
    facts: {
      inventory: "Process inventory",
      identity: "Process identity",
      "base-score": "Base score",
      attribution: "Software attribution",
      "runtime-state": "Scheduling target runtime state",
      "cpu-core-residency": "CPU core residency",
      "cpu-score": "CPU score",
      "gpu-score": "GPU score",
      "compute-score": "Compute score",
      "memory-cleanup": "Memory cleanup",
      "decision-target": "Decision target",
      "native-plan": "Native scheduling plan",
      CpuUsage: "Process CPU usage",
      MemoryUsage: "Process memory usage",
      GpuUsage: "Process GPU usage",
      GpuDedicatedMemory: "Process dedicated VRAM usage",
      RuntimeState: "Process runtime state"
    },
    reasons: {
      "dataset-not-current": "Data is not from the current sample",
      "unidentified-live-process": "A running process could not be identified",
      "inventory-overflow": "The process inventory exceeded its capacity",
      "emitted-count-mismatch": "Output count does not match the process inventory",
      "inventory-count-mismatch": "Inventory count does not match the enumerated count",
      "capture-count-mismatch": "Capture counts are inconsistent",
      "invalid-or-duplicate-inventory-identity": "The process inventory contains an invalid or duplicate process identity",
      "invalid-or-duplicate-process": "Invalid or duplicate process identity",
      "row-missing-or-invalid": "Data missing or invalid",
      "gpu-row-missing-or-invalid": "GPU data missing or invalid",
      "compute-runtime-state-missing": "Runtime state missing",
      "cpu-residency-not-covered": "CPU residency sampling does not cover this process",
      "cpu-residency-invalid": "CPU residency data invalid",
      "target-process-count-not-one": "The scheduling target does not correspond to exactly one process",
      "target-process-identity-missing": "The scheduling target has no process identity",
      "target-process-identity-mismatch": "The scheduling target's process identity is inconsistent",
      "duplicate-target-process-identity": "Several scheduling targets refer to the same process",
      "target-runtime-state-invalid": "The scheduling target's runtime state is invalid",
      "cleanup-baseline-score-missing": "Cleanup baseline score missing",
      "cleanup-current-process-facts-missing": "Current process data missing",
      "cleanup-software-maximum-base-score-missing": "The software's highest base score is missing",
      "ownership-software-attribution-mismatch": "Change ownership does not match the software attribution",
      "canonical-process-membership-mismatch": "The process does not match the software members in the score",
      "canonical-software-score-missing": "Software score missing",
      "live-identity-without-decision-facts": "A running process has no decision data",
      "canonical-process-not-projected": "The process score did not reach the scheduling input",
      "canonical-software-membership-incomplete": "The software score's members are incomplete"
    }
  }
};
