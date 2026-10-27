export const frontendWorkIds = {
  monitorMetrics: "monitor.metrics",
  monitorBars: "monitor.bars",
  monitorTable: "monitor.table",
  controlActual: "control.actual",
  managementInventory: "management.inventory",
  managementBrowserRuntimes: "management.browser-runtimes",
  optimizationReports: "optimization.reports",
  optimizationSmartStatus: "optimization.smart-status",
  detailsDeviceTopology: "details.device-topology",
  detailsCpuModel: "details.cpu-model",
  detailsGpuModel: "details.gpu-model",
  detailsSmartReport: "details.smart-report",
  detailsKernelLeaks: "details.kernel-leaks",
  targetedReportsPage: "targeted-reports.page",
  diskUsagePage: "disk-usage.page",
  // 设置页里读后端数据的分区：每个分区一项，可见区域是分区本身（同一时刻只建着当前分区）。
  settingsHardwareControl: "settings.hardware-control",
  settingsMemoryProtection: "settings.memory-protection",
  settingsReportRules: "settings.report-rules",
  settingsPerformanceOverlay: "settings.performance-overlay",
  settingsSystemIntegration: "settings.system-integration",
  settingsPublicServices: "settings.public-services",
  settingsTrustedAccess: "settings.trusted-access",
  settingsUpdates: "settings.updates"
} as const;

export type FrontendWorkId = typeof frontendWorkIds[keyof typeof frontendWorkIds];

// 消费端需要拿到“当前可见的来源 ID”的需求类别；需求 ID 由 frontendVisibilitySourceDemand 生成。
export const frontendVisibilitySourceKinds = {
  monitorCard: "monitor.card"
} as const;

export type FrontendVisibilitySourceKind =
  typeof frontendVisibilitySourceKinds[keyof typeof frontendVisibilitySourceKinds];

const knownFrontendWorkIds = new Set<FrontendWorkId>(Object.values(frontendWorkIds));

export function isFrontendWorkId(value: string): value is FrontendWorkId {
  return knownFrontendWorkIds.has(value as FrontendWorkId);
}
