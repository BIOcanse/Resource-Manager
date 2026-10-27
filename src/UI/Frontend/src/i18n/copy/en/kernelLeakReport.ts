import { plural } from "../../plural.ts";
import type { AppCopy } from "../zh/index.ts";

export const enKernelLeakReportCopy: Pick<AppCopy, "kernelLeakReport"> = {
  kernelLeakReport: {
    tab: "Kernel leak report",
    intro: "Lists processes whose kernel resources (currently process handles) keep growing and may be growing without bound, together with the evidence. A result only indicates a suspected leak; decide whether to act based on the evidence. Observation runs only while this page is shown and starts over when the page is opened again.",
    loading: "Loading the kernel leak report…",
    readFailed: "The kernel leak report could not be read.",
    capturedAt: "Captured at",
    suspectedItems: "Suspected items",
    emptyTitle: "No suspected kernel leaks found",
    emptyDetail: "This result is also shown while the observation time is not yet sufficient for a judgement. Suspected items appear here once they are found.",
    notObservedTitle: "No sample has been taken yet",
    unavailableTitle: "The kernel leak report is unavailable",
    failure: {
      NotObserved: "Observation has started. Results appear after the first sample.",
      SourceUnavailable: "The resource data of processes cannot be read.",
      CapacityExceeded: "The number of observed processes exceeds the capacity limit. This observation has stopped.",
      InvalidObservation: "The sampled data is invalid. This observation has stopped."
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
      currentValue: "Current count",
      growth: "Growth during observation",
      growthPerSecond: "Growth rate",
      observationTime: "Observation time",
      sampleCount: "Samples"
    },
    ratePerSecond: (value: string) => `${value}/s`,
    actionsPending: "The local service does not provide actions for this item yet."
  }
};
