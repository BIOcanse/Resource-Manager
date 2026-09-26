import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { extname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const sourceRoot = fileURLToPath(new URL("../src/", import.meta.url));
const readSource = (relativePath: string) => readFileSync(join(sourceRoot, relativePath), "utf8");

for (const retiredPath of [
  "frontendWork/FrontendPartitionController.ts",
  "frontendWork/frontendPartitionRegistration.ts",
  "frontendWork/frontendWorkMarker.ts",
  "frontendWork/frontendWorkVisibility.ts",
  "frontendWork/useFrontendWorkPoll.ts",
  "frontendWork/useFrontendPartitionController.tsx",
  "frontendWork/frontendWorkDemandRegistry.ts",
  "frontendWork/useFrontendWorkDemand.tsx",
  "resourceRegions/ResourceRegionBoundary.tsx",
  "resourceRegions/resourceRegionTypes.ts",
  "resourceRegions/resourceRegionVisibility.ts",
  "resourceRegions/useResourceRegionCoordinator.ts",
  "resourceRegions/useResourceRegionVisibility.ts"
]) {
  assert.equal(existsSync(join(sourceRoot, retiredPath)), false, `${retiredPath} must remain deleted`);
}

const activeSources = collectSourceFiles(sourceRoot)
  .map((path) => `${relative(sourceRoot, path)}\n${readFileSync(path, "utf8")}`)
  .join("\n");
for (const retiredPattern of [
  /frontendMark/,
  /FrontendPartition/,
  /data-frontend-mark/,
  /data-frontend-partitions/,
  /registerMountedWork/,
  /resourceRegions/,
  /ResourceManagerVisibleRegion/,
  /\/visible-regions/
]) {
  assert.doesNotMatch(
    activeSources,
    retiredPattern,
    `retired resource Mark mechanism ${retiredPattern} must stay absent`);
}

const controllerSource = readSource("frontendWork/useFrontendWorkController.ts");
const surfaceSource = readSource("frontendWork/frontendVisibilitySurface.ts");
const registrationSource = readSource("frontendWork/frontendVisibilityDemandRegistration.ts");
assert.match(controllerSource, /new FrontendVisibilityDemandController/);
assert.match(controllerSource, /registerVisibilityDemand/);
assert.match(controllerSource, /activeVisibilityDemandIds/);
assert.match(
  readSource("frontendWork/FrontendVisibilityDemandController.ts"),
  /requires at least one work ID/);
assert.match(controllerSource, /new IntersectionObserver/);
assert.match(controllerSource, /new MutationObserver/);
assert.match(controllerSource, /repairIntervalMilliseconds = 5_000/);
assert.match(
  controllerSource,
  /window\.setInterval\(\s*repairVisibilitySurfaces,\s*repairIntervalMilliseconds\)/,
  "the bounded repair pass must preserve direct visibility occupancy after observer loss");
assert.match(
  controllerSource,
  /resolveFrontendVisibilityBounds\(root\)[\s\S]*syncVisibilitySurfaces\(visibilityBounds\)[\s\S]*isFrontendSurfaceVisible/,
  "the repair pass must re-evaluate every registered visibility surface");
assert.match(
  controllerSource,
  /if \(!parsed\) \{[\s\S]*visibilitySurfaces\.delete\(element\)[\s\S]*unobserve\(element\)/,
  "an invalidated dynamic surface must release its direct occupancy immediately");
assert.match(controllerSource, /host\.visibility:hidden/);
assert.match(controllerSource, /host\.visibility:visible/);
assert.match(controllerSource, /document\.visibilityState/);
assert.doesNotMatch(
  controllerSource,
  /visible-regions|ResourceManagerVisibleRegion|TouchResources|isMarkerReferenced/,
  "frontend visibility occupancy must remain local to frontend work");
assert.match(surfaceSource, /data-frontend-visibility-surface/);
assert.match(surfaceSource, /data-frontend-visibility-demands/);
assert.doesNotMatch(surfaceSource, /mark|partition/i);
assert.match(registrationSource, /registerVisibilityDemand/);
assert.match(registrationSource, /onCleanup/);

for (const relativePath of [
  "features/management/components/ManagementPage.tsx",
  "features/management/components/MigrationPanel.tsx",
  "components/OptimizationPage.tsx",
  "components/CpuTopologyDiagram.tsx",
  "features/deviceTopology/DeviceTopologyView.tsx",
  "components/HostManagerSmartCoordinatorDetailsReport.tsx"
]) {
  const source = readSource(relativePath);
  assert.match(
    source,
    /frontendVisibilitySurface/,
    `${relativePath} must expose its real visible work surface`);
  assert.match(
    source,
    /useFrontendVisibilityDemand|FrontendVisibilityDemandBinding/,
    `${relativePath} must bind that surface to direct work demand`);
}

const managementSource = readSource("features/management/components/ManagementPage.tsx");
assert.match(managementSource, /frontendVisibilityDemandId\(\s*"management\.component"/);
assert.match(managementSource, /frontendVisibilityDemandId\("management\.software"/);
assert.match(managementSource, /FrontendVisibilityDemandBinding[\s\S]*managementInventory/);

const optimizationSource = readSource("components/OptimizationPage.tsx");
assert.match(optimizationSource, /frontendVisibilityDemandId\("optimization\.report"/);
assert.match(optimizationSource, /FrontendVisibilityDemandBinding[\s\S]*optimizationReports/);

const gpuSchedulingSource = readSource("components/GpuSchedulingModel.tsx");
assert.doesNotMatch(gpuSchedulingSource, /setInterval\s*\(|useFrontendWorkPoll/);
assert.match(gpuSchedulingSource, /useFrontendVisibilityDemand/);
assert.match(gpuSchedulingSource, /frontendVisibilitySurface/);
assert.match(
  gpuSchedulingSource,
  /frontendWork\.isNeeded\(frontendWorkIds\.detailsGpuModel\)/);
assert.match(gpuSchedulingSource, /gpuSpecializedTelemetry\.subscribe/);
assert.match(gpuSchedulingSource, /metricSnapshot\.subscribe/);
assert.match(gpuSchedulingSource, /resourceMonitor\.subscribe/);

const smartReportSource = readSource(
  "components/HostManagerSmartCoordinatorDetailsReport.tsx");
assert.doesNotMatch(smartReportSource, /setInterval\s*\(|useFrontendWorkPoll|getHostManagerRollbackState|\.refresh\s*\(/);
assert.match(smartReportSource, /sources\.smartCoordinatorState\.subscribe/);

const cpuTopologySource = readSource("components/CpuTopologyDiagram.tsx");
assert.doesNotMatch(cpuTopologySource, /setInterval\s*\(|useFrontendWorkPoll|createResource\s*\(/);
assert.match(cpuTopologySource, /frontendRuntime\.sources\.cpuTopology\.subscribe/);
assert.match(cpuTopologySource, /frontendRuntime\.sources\.cpuResidency\.subscribe/);
assert.doesNotMatch(
  cpuTopologySource,
  /useSource\(frontendRuntime\.sources\.(?:cpuTopology|cpuResidency)/);
assert.match(cpuTopologySource, /frontendWork\.isNeeded\(frontendWorkIds\.detailsCpuModel\)/);

const deviceTopologyStoreSource = readSource("features/deviceTopology/deviceTopologyStore.ts");
assert.doesNotMatch(deviceTopologyStoreSource, /setInterval\s*\(|useFrontendWorkPoll/);
assert.match(deviceTopologyStoreSource, /runtime\.sources\.deviceTopology\.subscribe/);
assert.doesNotMatch(deviceTopologyStoreSource, /useSource\(|refreshIntervalMs/);
assert.match(
  deviceTopologyStoreSource,
  /frontendWork\.isNeeded\(frontendWorkIds\.detailsDeviceTopology\)/);

const managementWorkspaceSource = readSource("features/management/ManagementWorkspace.tsx");
assert.match(
  managementWorkspaceSource,
  /frontendVisibilitySurface\([\s\S]*management\.browser-runtime[\s\S]*FrontendVisibilityDemandBinding[\s\S]*managementBrowserRuntimes[\s\S]*ObservationStateBoundary[\s\S]*BrowserRuntimePage/,
  "browser runtime demand must exist before its observation content boundary");
assert.doesNotMatch(
  readSource("features/browserRuntimes/BrowserRuntimePage.tsx"),
  /useFrontendVisibilityDemand|FrontendVisibilityDemandBinding|frontendVisibilitySurface/,
  "browser runtime content must not duplicate its workspace visibility owner");

const monitorPageSource = readSource("features/monitor/MonitorPage.tsx");
assert.equal(
  monitorPageSource.match(/<MonitorWorkRegion/g)?.length,
  3,
  "monitor must retain three semantic work regions");
assert.equal(
  monitorPageSource.match(/renderWhenUnavailable/g)?.length,
  3,
  "continuous monitor values must remain renderable while their transport reconnects");
assert.doesNotMatch(monitorPageSource, /demandId=|frontendWorkIds\./);
const monitorRegionSource = readSource("features/monitor/MonitorWorkRegion.tsx");
assert.doesNotMatch(
  monitorRegionSource,
  /frontendVisibilitySurface|FrontendVisibilityDemandBinding|IntersectionObserver/);
assert.match(monitorRegionSource, /aria-label=\{props\.label\}/);
for (const relativePath of [
  "features/monitor/Dashboard.tsx",
  "features/resourceBreakdown/ResourceBreakdown.tsx",
  "features/resourceTable/ResourceTable.tsx"
]) {
  assert.doesNotMatch(
    readSource(relativePath),
    /useFrontendVisibilityDemand|FrontendVisibilityDemandBinding|frontendVisibilitySurface/,
    `${relativePath} must not duplicate its enclosing visibility owner`);
}

assert.doesNotMatch(
  readSource("features/settings/SettingsPage.tsx"),
  /useFrontendVisibilityDemand|FrontendVisibilityDemandBinding|frontendVisibilitySurface/,
  "settings sections with no continuous work must not register empty visibility owners");

const refreshSchedulerSource = readSource("app/usePageRefreshScheduler.ts");
assert.match(
  refreshSchedulerSource,
  /options\.selfSchedulingSource\.subscribe\(\s*selfSchedulingSubscriptionIntervalMs/,
  "self-scheduling must be one backend current-value subscription");
assert.match(
  refreshSchedulerSource,
  /options\.localSystemSource\.subscribe\(\s*intervalMs/,
  "local system status must be one independent backend current-value subscription");
assert.match(
  refreshSchedulerSource,
  /if \(!options\.performanceSettings\(\)\) \{\s*return;/,
  "configured subscriptions must not start with a provisional pre-settings frequency");
assert.doesNotMatch(
  refreshSchedulerSource,
  /window\.setInterval|schedulerTickMs|lastRunAt|shouldRun\(|\.refresh\(|useSource\(|ObservationState/,
  "page data must not regain frontend polling or continuous-value state machines");
assert.match(refreshSchedulerSource, /scheduleVisiblePageRefresh/);
assert.match(refreshSchedulerSource, /refreshVisiblePageData/);

const monitorStoreSource = readSource("stores/monitorStore.ts");
assert.match(monitorStoreSource, /options\.resourceMonitorSource\.subscribe/);
assert.match(monitorStoreSource, /options\.metricSnapshotSource\.subscribe/);
assert.doesNotMatch(
  monitorStoreSource,
  /resourceMonitorDemand|monitorResourceBarsNeeded|monitorResourceTableNeeded|monitorSnapshotNeeded|paused/);

const appSource = readSource("App.tsx");
assert.equal(
  appSource.match(/activePage\(\) === "monitor" && frontendWork\.frontendVisible\(\)/g)?.length,
  3,
  "monitor push subscriptions must be owned by the visible monitor page, not section intersection");
assert.doesNotMatch(
  appSource,
  /frontendWork\.isNeeded\(frontendWorkIds\.monitor/);

for (const relativePath of [
  "types.ts",
  "stores/settingsStore.ts",
  "features/settings/SettingsPage.tsx",
  "features/settings/components/AppearanceSettingsSection.tsx",
  "i18n/settingsTypes.ts",
  "i18n/settingsLocaleFactory.ts"
]) {
  assert.doesNotMatch(
    readSource(relativePath),
    /settingsDisplayOutput|outputDisplayProfiles|sceneReferenceWhite|outputMinimumNits|outputMaximumNits|outputColorGamut|outputToneMappingCurve|finalOutput/,
    `${relativePath} must not retain retired display-output settings`);
}

function collectSourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return collectSourceFiles(path);
    }
    return [".ts", ".tsx", ".css"].includes(extname(entry.name)) ? [path] : [];
  });
}
