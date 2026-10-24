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
  targetedReportsPage: "targeted-reports.page"
} as const;

export type FrontendWorkId = typeof frontendWorkIds[keyof typeof frontendWorkIds];

const knownFrontendWorkIds = new Set<FrontendWorkId>(Object.values(frontendWorkIds));

export function isFrontendWorkId(value: string): value is FrontendWorkId {
  return knownFrontendWorkIds.has(value as FrontendWorkId);
}
