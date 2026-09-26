import type { ResourceTableViewMode } from "../../../types.ts";
import type { AppCopy } from "../zh/index.ts";

export const enMonitorCopy: Pick<
  AppCopy,
  "monitorPage" | "dashboard" | "resourceBreakdown" | "resourceTable"
> = {
  monitorPage: {
    addCard: "Add card",
    addCardLabel: "Add monitoring card",
    cancel: "Cancel",
    cancelEditLabel: "Cancel dashboard layout editing",
    save: "Save",
    edit: "Edit",
    saveLayoutLabel: "Save dashboard layout",
    editLayoutLabel: "Edit dashboard layout",
    layoutEditUnavailable: "Layout editing is unavailable",
    layoutEditor: "Dashboard layout editor",
    dashboardQuarantined: "The dashboard configuration was damaged and quarantined; a saved safe default is in use.",
    dashboardRecovered: "The dashboard configuration was restored from the last valid copy.",
    metricCatalog: "Metric catalog",
    liveMetrics: "Live metrics",
    resourceUsage: "Resource usage",
    resourceList: "Resource list"
  },
  dashboard: {
    panel: "Monitoring dashboard",
    emptyMetric: "Empty metric",
    primarySlot: (ordinal: number) => `Card ${ordinal} primary metric`,
    detailSlot: (ordinal: number, index: number) => `Card ${ordinal} detail metric ${index}`,
    addMetric: (slot: string) => `Add a metric to ${slot}`,
    replaceMetric: (slot: string, metric: string) => `Replace ${slot}: ${metric}`,
    removeMetric: (slot: string, metric: string) => `Remove ${slot}: ${metric}`,
    catalogUnavailable: "Metric catalog unavailable",
    metricUnavailable: "Metric unavailable"
  },
  resourceBreakdown: {
    panel: "Software resource usage",
    save: "Save",
    edit: "Edit",
    statusFallback: "No data",
    scaleCapacity: "Including free",
    scaleActive: "Current usage",
    selectionLabel: (metric: string) => `${metric} software usage selection`,
    empty: "Free",
    emptyPercent: "Free share",
    systemPercent: "System share",
    categoryCount: (count: number) => `${count} categories`,
    processCount: (count: number) => `${count} processes`,
    noAttributableProcess: "No attributable process",
    noPidCategory: "No PID category",
    providerNotSplit: "Shared system usage that cannot be attributed to a single process.",
    etwSupplement: "Processes identified, but not yet attributed to specific software.",
    softwareInnerPercent: "Share within software",
    gpuUsageLabel: (index: string) => `GPU${index} utilization`,
    gpuVramLabel: (index: string) => `GPU${index} VRAM usage`
  },
  resourceTable: {
    panel: "Resource list",
    performance: "Performance",
    searchPlaceholder: "Search name, PID, status",
    viewLabel: "Resource list view",
    save: "Save",
    edit: "Edit",
    expandProcesses: "Expand processes",
    collapseProcesses: "Collapse processes",
    modes: {
      software: "By software",
      process: "By process",
      performance: "Performance"
    } as Record<ResourceTableViewMode, string>,
    summaryRow: {
      name: "Total usage",
      status: {
        warming: "Warming up",
        ready: "Current sample",
        stale: "Last sample",
        failed: "Sampling failed"
      }
    },
    systemResidualRow: (metric: string) => `System/driver reserved · ${metric}`,
    columns: {
      name: "Name",
      pid: "PID",
      status: "Status",
      user: "User",
      architecture: "Architecture",
      cpu: "CPU",
      memory: "Memory",
      disk: "Disk",
      network: "Network"
    },
    gpuUsageLabel: (index: string) => `GPU${index} utilization`,
    gpuVramLabel: (index: string) => `GPU${index} VRAM usage`
  }
};
