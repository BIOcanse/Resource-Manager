import type { ResourceTableViewMode } from "../../../types.ts";
import type { AppCopy } from "../zh/index.ts";
import { plural } from "../../plural.ts";

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
    slotLabel: (card: number, index: number) => `Card ${card} item ${index}`,
    addSlot: (card: number) => `Add a metric to card ${card}`,
    resizeSlot: (slot: string, metric: string) => `Resize ${slot}: ${metric}`,
    slotSize: { small: "Small", medium: "Medium", large: "Large", full: "Full" },
    slotSizeHint: { small: "1 unit", medium: "2 units", large: "3 units", full: "Fills the card" },
    currentSize: "Current size",
    trendLabel: (label: string, count: number) => plural("en-US", count, { one: `${label}: trend of the last {count} reading`, other: `${label}: trend of the last {count} readings` }),
    trendLow: "Low",
    trendHigh: "High",
    replaceMetric: (slot: string, metric: string) => `Replace ${slot}: ${metric}`,
    removeMetric: (slot: string, metric: string) => `Remove ${slot}: ${metric}`,
    catalogUnavailable: "Metric catalog unavailable",
    metricUnavailable: "Metric unavailable",
    deviceAbsent: "Device not present",
    deviceAbsentHint: "This device isn't present right now. The setting is kept, and the value comes back with the device.",
    valueUnavailable: "Unavailable",
    needsComponent: "Needs component",
    noReadingHint: "No reading right now",
    noReading: "No reading",
    waitingReading: "Waiting for the first reading",
    notInCatalogHint: "This item isn't in the metric catalog",
    namedDevice: (kind: string, name: string) => `${kind} · ${name}`,
    numberedDevice: (kind: string, ordinal: number) => `${kind} ${ordinal}`,
    readingSource: {
      preferred: "Preferred",
      preferredHint: "The most accurate source for this reading on this computer.",
      pending: "Pending",
      pendingHint: "The most accurate source for this reading on this computer has not been confirmed yet."
    },
    deviceKind: {
      host: "System",
      cpu: "Processor",
      cpu_package: "Processor package",
      cpu_die: "Processor die",
      cpu_core: "Processor core",
      cpu_logical_processor: "Logical processor",
      cpu_platform: "Processor platform",
      numa_node: "NUMA node",
      memory: "Memory",
      virtual_memory: "Virtual memory",
      gpu_adapter: "Graphics card",
      gpu_engine: "GPU engine",
      gpu_fan_tuning_group: "GPU fan control group",
      npu: "NPU",
      npu_engine: "NPU engine",
      storage: "Disk",
      network_interface: "Network adapter",
      fan: "Fan",
      battery: "Battery"
    }
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
    categoryCount: (count: number) => plural("en-US", count, { one: "{count} category", other: "{count} categories" }),
    processCount: (count: number) => plural("en-US", count, { one: "{count} process", other: "{count} processes" }),
    noAttributableProcess: "No attributable process",
    noPidCategory: "No PID category",
    residualVram: "GPU / WDDM / desktop composition (unclassified)",
    residualMemory: "System / kernel / drivers",
    residualCommit: "System commit / kernel",
    residualSystem: "System / drivers",
    etwResidualProcess: (name: string) => `${name} · Unattributed GPU process (DXGKrnl/VidMm)`,
    providerNotSplit: "Shared system usage that cannot be attributed to a single process.",
    etwSupplement: "Processes identified, but not yet attributed to specific software.",
    softwareInnerPercent: "Share within software",
    memoryBasis: "System: physical memory used / capacity. Process segments: private resident bytes; shared pages and system usage remain unattributed. Unavailable readings do not produce estimated process segments.",
    memoryTableBasis: "System: physical memory used / physical capacity. Processes: private working set, the resident pages that belong to that process alone, without shared pages; shared pages and system usage stay unattributed, so the processes add up to less than the system total.",
    diskBasis: "Completed physical disk transfers; cache hits and network file I/O are excluded. Transfers with unknown owners remain unattributed; write-cache flushes may belong to the system process.",
    commitBasis: "System: committed memory / current commit limit. Processes: private commit. Commit includes RAM and page-file backing, not actual page-file occupancy.",
    vramBasis: "Device: resident VRAM / physical capacity. Processes: resident memory; shared memory is divided among referencing processes.",
    gpuUsageBasis: "Device: measured utilization. Software and processes: utilization apportioned by activity on the busiest GPU engine.",
    gpuUsageLabel: (index: string) => `GPU${index} utilization`,
    gpuVramLabel: (index: string) => `GPU${index} resident VRAM`
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
        failed: "Sampling failed"
      }
    },
    systemResidualRow: (metric: string) => `System/driver reserved · ${metric}`,
    columnSourceLabel: (column: string) => `${column} source`,
    memoryQuantities: {
      working_set_including_shared: "Working set (incl. shared pages)",
      private_working_set: "Private working set",
      private_commit: "Private commit"
    },
    memoryColumnBySource: (quantity: string) => `Memory · ${quantity}`,
    memorySourceBasis: {
      working_set_including_shared: "System: physical memory used / physical capacity. Processes: working set, including pages shared with other processes; a shared page counts in every process that uses it, so the processes can add up to more than the system total, and then there is no system/driver difference.",
      private_commit: "System: system commit charge / commit limit. Processes: private commit. The difference between the processes and the system commit charge is in the system/driver reserved row. Commit includes RAM and page-file backing, not actual page-file occupancy."
    },
    columns: {
      name: "Name",
      pid: "PID",
      status: "Status",
      user: "User",
      architecture: "Architecture",
      cpu: "CPU",
      memory: "Resident memory",
      disk: "Disk",
      network: "Network"
    },
    gpuUsageLabel: (index: string) => `GPU${index} utilization`,
    gpuVramLabel: (index: string) => `GPU${index} resident VRAM`
  }
};
