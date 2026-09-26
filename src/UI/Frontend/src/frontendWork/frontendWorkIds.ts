export const frontendWorkIds = {
  managementInventory: "management.inventory",
  managementBrowserRuntimes: "management.browser-runtimes",
  migrationRoots: "management.migration.roots",
  migrationRecords: "management.migration.records",
  migrationSessions: "management.migration.sessions",
  optimizationReports: "optimization.reports",
  optimizationSmartStatus: "optimization.smart-status",
  detailsDeviceTopology: "details.device-topology",
  detailsCpuModel: "details.cpu-model",
  detailsGpuModel: "details.gpu-model",
  detailsSmartReport: "details.smart-report"
} as const;

export type FrontendWorkId = typeof frontendWorkIds[keyof typeof frontendWorkIds];

const knownFrontendWorkIds = new Set<FrontendWorkId>(Object.values(frontendWorkIds));

export function isFrontendWorkId(value: string): value is FrontendWorkId {
  return knownFrontendWorkIds.has(value as FrontendWorkId);
}
