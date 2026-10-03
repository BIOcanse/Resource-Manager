import type { AppCopy } from "../zh/index.ts";

export const enTargetedReportCopy: Pick<AppCopy, "targetedReport"> = {
  targetedReport: {
    tab: "Targeted reports",
    title: "Targeted monitoring reports",
    intro: "Record application Present frame times and resources for all processes of this software.",
    maximumSeconds: "Maximum recording duration (seconds)",
    start: "Start recording",
    stop: "Stop recording",
    recording: "Recording",
    completed: "Completed",
    refresh: "Refresh reports",
    empty: "No recordings yet.",
    loading: "Loading report…",
    failed: "Recording report operation failed.",
    delete: "Delete report",
    confirmDelete: "Delete this recording report?",
    exportJson: "Export JSON",
    exportCsv: "Export CSV",
    frameTime: "Frame time over time",
    distribution: "FPS distribution",
    resources: "Resource curves",
    noFrames: "No frame intervals are available.",
    applicationPresent: "Application Present frames",
    incomplete: "Capture has gaps; statistics may be low.",
    averageFps: "Average FPS",
    onePercentLow: "1% Low",
    pointOnePercentLow: "0.1% Low",
    p50: "P50 frame time",
    p95: "P95 frame time",
    p99: "P99 frame time",
    maxFrameTime: "Maximum frame time",
    frameCount: "Frame intervals",
    software: "Target software",
    system: "System",
    seconds: "seconds",
    stopReasons: {
      user: "Stopped manually",
      processExit: "Processes exited",
      maxDuration: "Maximum duration reached",
      interrupted: "Service interrupted",
      captureError: "Capture error"
    }
  }
};
