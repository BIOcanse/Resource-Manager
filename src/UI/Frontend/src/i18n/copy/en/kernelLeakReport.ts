import { plural } from "../../plural.ts";
import type { AppCopy } from "../zh/index.ts";

export const enKernelLeakReportCopy: Pick<AppCopy, "kernelLeakReport"> = {
  kernelLeakReport: {
    tab: "Kernel leak report",
    intro: "While it runs, the local service keeps observing the kernel resources held by each process (currently process handles) and lists the processes whose resources keep growing and may be growing without bound, together with the evidence. A result only indicates a suspected leak; decide whether to act based on the evidence.",
    loading: "Loading the kernel leak report…",
    readFailed: "The kernel leak report could not be read.",
    capturedAt: "Captured at",
    suspectedItems: "Suspected items",
    historyItems: "History entries",
    unrecordedItems: "Not recorded (limit reached)",
    items: "Report entries",
    historyEntry: "History",
    notObservedTitle: "Kernel leaks are not being observed",
    notObservedDetail: "Kernel leak observation is not running: it may be turned off, its observation plan may not be published yet, or it has stopped. Until it runs, no samples are taken and no report is produced.",
    observingTitle: "Observing",
    observingDetail: "Evaluation starts once this run's observation time and sample count both meet the requirements.",
    emptyTitle: "No suspected kernel leaks found",
    emptyDetail: "This run's observation time and sample count meet the requirements, and no kernel resource keeps growing.",
    unavailableTitle: "This observation run failed",
    failure: {
      SourceUnavailable: "The resource data of processes could not be read.",
      CapacityExceeded: "The number of observed processes exceeded the capacity limit, so this run was stopped.",
      InvalidObservation: "The sample data was invalid, so this run was stopped."
    },
    observation: {
      title: "Current run",
      startedAt: "Run started",
      elapsed: "Observation time",
      samples: "Samples",
      notStarted: "Not sampled yet",
      reachedOfRequired: (reached: string, required: string) => `${reached} (required: ${required})`
    },
    previousRun: {
      title: "Previous run failed",
      reason: "Reason",
      startedAt: "Run started",
      failedAt: "Failed at"
    },
    restartPolicy: {
      NextInterval: "The local service starts a new run automatically at the next sampling interval."
    },
    unavailableHolders: (count: number) => plural("en-US", count, {
      one: "The resource data of {count} process could not be read; it is not included in this judgement.",
      other: "The resource data of {count} processes could not be read; they are not included in this judgement."
    }),
    resourceKind: {
      ProcessHandles: "Process handles"
    },
    holder: {
      processId: "PID",
      productName: "Product name",
      services: "Services",
      imagePath: "Image path",
      noProductName: "Not provided",
      noServices: "No matching service",
      unavailable: "Cannot be read",
      processEnded: "Process ended"
    },
    evidence: {
      currentValue: "Current value",
      growth: "Growth during observation",
      growthPerSecond: "Growth rate",
      observationTime: "Observation time",
      sampleCount: "Samples",
      firstObserved: "First detected",
      lastObserved: "Last detected"
    },
    ratePerSecond: (value: string) => `${value}/s`,
    details: {
      button: "Details",
      back: "Back to report list",
      title: (name: string) => `${name} details`,
      holderSection: "Holder process",
      evidenceSection: "Evidence",
      state: "State",
      active: "Active",
      resourceKind: "Resource type",
      processName: "Process name",
      startKey: "Process start key",
      unavailableReason: "Reason unavailable"
    },
    actions: {
      title: "Actions",
      name: {
        RestartHolder: "Restart holder",
        ForceReleaseHandles: "Force release handles"
      },
      riskLevel: {
        Warning: "Warning",
        High: "High"
      },
      riskText: (level: string, risk: string) => `Risk: ${level}. ${risk}`,
      riskLabel: (name: string) => `Risk: ${name}`
    }
  }
};
