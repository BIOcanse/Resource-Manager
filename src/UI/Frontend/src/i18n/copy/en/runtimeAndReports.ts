import type { AppCopy } from "../zh/index.ts";
import { plural } from "../../plural.ts";

export const enRuntimeAndReportsCopy: Pick<AppCopy, "smartReport" | "reportHistory" | "browserRuntime"> = {
  smartReport: {
    processTarget: (name: string, pid: number) => `${name} (process ${pid})`,
    loading: "Loading",
    unavailable: "State unavailable",
    refresh: "Refresh",
    completed: "Completed",
    needsAttention: "Needs attention",
    releasedMemory: "Memory released",
    releasedVram: "VRAM released",
    empty: "No smart scheduling records yet.",
    details: "Details",
    fallbackTarget: "Scheduling",
    detailTitle: (target: string) => `${target} details`,
    readFailed: "Reading the smart scheduling report failed",
    showAllRecords: (count: number) => plural("en-US", count, { one: "Show {count} record", other: "Show all {count} records" }),
    showRecentRecords: (count: number) => plural("en-US", count, { one: "Show only the latest {count} record", other: "Show only the latest {count} records" }),
    section: {
      result: "Scheduling result",
      execution: "Execution"
    },
    field: {
      target: "Item",
      change: "Change",
      affectedResource: "Affected resource",
      state: "State",
      updatedAt: "Updated",
      accepted: "Succeeded",
      timedOut: "Timed out",
      rejected: "Not executed",
      releasedMemory: "Memory released",
      releasedVram: "VRAM released"
    },
    itemCount: (count: number) => `${count}`,
    kind: {
      softwareResourceSettings: "Software resource settings",
      gpuSelection: "GPU selection",
      runtimeGpuSwitch: "Runtime GPU switch",
      cpuCoreAllocation: "CPU core allocation",
      resourceOptimization: "Resource optimization",
      schedulingAdjustment: "Scheduling adjustment"
    },
    // 智能调度历史（docs/report_lifecycle_2026-10-28.md“智能调度记录”）。
    history: {
      title: "History",
      loading: "Reading history",
      readFailed: "Could not read the smart scheduling history",
      empty: "No history records.",
      retentionOff: "Keeping smart scheduling history is off, so records are not kept after they are restored. You can turn it on in Settings → Runtime Policy.",
      loadMore: "Load earlier records",
      field: {
        sequence: "Sequence",
        recordedAt: "Recorded",
        appliedAt: "Applied",
        restoredAt: "Restored",
        restoreResult: "Restore result"
      },
      restoreResult: {
        restored: "Restored",
        targetExited: "Target exited",
        ownershipLost: "Changed by another program; no longer restored",
        failed: "Restore failed",
        unknown: "Result not recorded"
      }
    },
    resource: {
      cpu: "Processor",
      gpu: "Graphics processor",
      memory: "Memory",
      vram: "VRAM",
      disk: "Disk",
      network: "Network",
      software: "Software resources"
    }
  },
  // 报告页的“清空历史”：一次删除这类报告的全部历史条目，活动条目保留（src/features/reportHistory）。
  reportHistory: {
    clear: "Clear history",
    clearing: "Clearing",
    confirmTitle: "Clear history entries?",
    confirm: "Clear",
    confirmMessage: {
      optimizationReports: (count: string) => `History reports to delete: ${count}. Active reports are kept.`,
      kernelLeaks: (count: string) => `History entries to delete: ${count}. Active entries are kept.`,
      memoryProtection: (count: string) => `Memory protection action records to delete: ${count}.`,
      targetedRecordings: (count: string) => `Completed recording reports to delete: ${count}. A recording in progress is kept.`,
      smartRecords: "All smart scheduling history records will be deleted. Records waiting to be restored are kept."
    },
    details: {
      optimizationReports: ["This cannot be undone. If the same issue occurs again, it will be recorded again.", "Trusted software and devices remain trusted."],
      kernelLeaks: ["This cannot be undone. If the same issue occurs again, it will be recorded again."],
      memoryProtection: ["This cannot be undone.", "Slice files are not deleted; they stay on the disk of the disk reserve until you remove them."],
      targetedRecordings: ["These reports and their frame intervals and resource data will be permanently deleted. This cannot be undone."],
      smartRecords: ["This cannot be undone. Record sequence numbers do not start over."]
    },
    clearedTitle: "History cleared",
    cleared: (count: string) => `History entries deleted: ${count}.`,
    failed: "Could not clear the history."
  },
  browserRuntime: {
    description: "Review the web interface runtime and the browsers installed on this PC.",
    downloadShared: "Download the shared runtime",
    refreshing: "Refreshing",
    refresh: "Refresh",
    sharedRuntimeTitle: "Current shared runtime",
    sharedRuntimeDescription: "Resource Manager shares one system runtime with other WebView2 software.",
    openOfficialSource: "Open the official source",
    noSharedRuntime: "No shared runtime detected",
    noSharedRuntimeDetail: "You can install the official shared runtime; existing browsers stay listed as fallbacks.",
    inUse: "In use",
    installedBrowsersTitle: "Installed browsers",
    installedBrowsersDescription: "Software that supports external browsers can use these.",
    itemCount: (count: number) => plural("en-US", count, { one: "{count} item", other: "{count} items" }),
    noBrowsers: "No reusable browser found.",
    currentFallback: "Current fallback",
    fallbackRuntimeName: "Runtime",
    detailTitle: (name: string) => `${name} details`,
    version: (value: string) => `Version ${value}`,
    unknown: "Unknown",
    detailsOf: (name: string) => `${name} details`,
    sharedRuntimePurpose: "A shared interface runtime used by WebView2 software on this PC.",
    externalBrowserPurpose: "Opens web interfaces for software that supports external browsers; it cannot be used as an embedded WebView2 runtime.",
    chromiumReusePurpose: "Reusable by software that supports external Chromium browsers.",
    runtimeInfoTitle: "Runtime information",
    field: {
      kind: "Kind",
      version: "Version",
      state: "State",
      source: "Source",
      runtimeDirectory: "Runtime directory",
      executable: "Executable"
    },
    available: "Available",
    kind: {
      webView2Runtime: "Shared WebView2 runtime",
      chromiumBrowser: "Chromium browser",
      geckoBrowser: "Gecko browser"
    },
    source: {
      systemMachine: "System (all users)",
      systemUser: "System (current user)",
      managed: "Downloaded by Resource Manager",
      browserRegistration: "System browser registration",
      knownInstall: "Local install directory",
      local: "This PC"
    }
  }
};