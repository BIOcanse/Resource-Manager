import type { AppCopy } from "../zh/index.ts";

export const enRuntimeAndReportsCopy: Pick<AppCopy, "smartReport" | "browserRuntime"> = {
  smartReport: {
    panel: "Smart scheduling report",
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
  browserRuntime: {
    title: "Runtime management",
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
    itemCount: (count: number) => `${count} items`,
    noBrowsers: "No reusable browser found.",
    currentFallback: "Current fallback",
    fallbackRuntimeName: "Runtime",
    detailTitle: (name: string) => `${name} details`,
    version: (value: string) => `Version ${value}`,
    unknown: "Unknown",
    details: "Details",
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