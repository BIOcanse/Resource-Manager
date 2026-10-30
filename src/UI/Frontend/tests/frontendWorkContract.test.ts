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
assert.match(controllerSource, /activeVisibilitySourceIds/);
// 看板卡片的可见 ID 由控制器按来源类别直接交出，不能在消费端从需求 ID 字符串拼接或反解。
assert.doesNotMatch(readSource("App.tsx"), /monitor\.card\.|decodeURIComponent/);
assert.match(
  readSource("frontendWork/FrontendVisibilityDemandController.ts"),
  /requires at least one work ID/);
assert.match(controllerSource, /new IntersectionObserver/);
assert.match(controllerSource, /new MutationObserver/);
assert.doesNotMatch(controllerSource, /getBoundingClientRect|getClientRects|setInterval/,
  "visibility is observer-owned, without synchronous geometry repair or polling");
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
  "features/optimization/OptimizationPage.tsx",
  "features/details/CpuTopologyDiagram.tsx",
  "features/deviceTopology/DeviceTopologyView.tsx",
  "features/details/HostManagerSmartCoordinatorDetailsReport.tsx"
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

const optimizationSource = readSource("features/optimization/OptimizationPage.tsx");
assert.match(optimizationSource, /frontendVisibilityDemandId\("optimization\.report"/);
assert.match(optimizationSource, /FrontendVisibilityDemandBinding[\s\S]*optimizationReports/);

const gpuSchedulingSource = readSource("features/gpuScheduling/GpuSchedulingModel.tsx");
assert.doesNotMatch(gpuSchedulingSource, /setInterval\s*\(|useFrontendWorkPoll/);
assert.match(gpuSchedulingSource, /useFrontendVisibilityDemand/);
assert.match(gpuSchedulingSource, /frontendVisibilitySurface/);
assert.match(
  gpuSchedulingSource,
  /frontendWork\.isNeeded\(frontendWorkIds\.detailsGpuModel\)/);
assert.match(gpuSchedulingSource, /usePushSource\(runtime\.sources\.gpuSpecializedTelemetry,/);
assert.match(gpuSchedulingSource, /usePushSource\(runtime\.sources\.metricSnapshot,/);
assert.match(gpuSchedulingSource, /usePushSource\(runtime\.sources\.resourceMonitor,/);

const smartReportSource = readSource(
  "features/details/HostManagerSmartCoordinatorDetailsReport.tsx");
assert.doesNotMatch(smartReportSource, /setInterval\s*\(|useFrontendWorkPoll|getHostManagerRollbackState|\.refresh\s*\(/);
assert.match(smartReportSource, /usePushSource\(frontendRuntime\.sources\.smartCoordinatorState,/);

const cpuTopologySource = readSource("features/details/CpuTopologyDiagram.tsx");
assert.doesNotMatch(cpuTopologySource, /setInterval\s*\(|useFrontendWorkPoll|createResource\s*\(/);
assert.match(cpuTopologySource, /usePushSource\(frontendRuntime\.sources\.cpuTopology,/);
assert.match(cpuTopologySource, /usePushSource\(frontendRuntime\.sources\.cpuResidency,/);
assert.doesNotMatch(
  cpuTopologySource,
  /useSource\(frontendRuntime\.sources\.(?:cpuTopology|cpuResidency)/);
assert.match(cpuTopologySource, /frontendWork\.isNeeded\(frontendWorkIds\.detailsCpuModel\)/);

const deviceTopologyStoreSource = readSource("features/deviceTopology/deviceTopologyStore.ts");
assert.doesNotMatch(deviceTopologyStoreSource, /setInterval\s*\(|useFrontendWorkPoll/);
assert.match(deviceTopologyStoreSource, /usePushSource\(runtime\.sources\.deviceTopology,/);
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
assert.match(
  monitorRegionSource,
  /frontendVisibilitySurface/);
assert.match(monitorRegionSource, /useFrontendVisibilityDemand/);
assert.match(monitorRegionSource, /aria-label=\{props\.label\}/);
for (const relativePath of [
  "features/resourceBreakdown/ResourceBreakdown.tsx",
  "features/resourceTable/ResourceTable.tsx"
]) {
  assert.doesNotMatch(
    readSource(relativePath),
    /useFrontendVisibilityDemand|FrontendVisibilityDemandBinding|frontendVisibilitySurface/,
    `${relativePath} must not duplicate its enclosing visibility owner`);
}

const dashboardSource = readSource("features/monitor/Dashboard.tsx");
assert.match(dashboardSource, /frontendVisibilitySourceDemand\(frontendVisibilitySourceKinds\.monitorCard, cardId\)/,
  "each dashboard card must declare its own visible demand");
// 卡片按 id 渲染（改槽位时卡片不重建）：cardId 就是这张卡片的 card.id，由下面两条锁住。
assert.match(dashboardSource, /const cardIds = createMemo\(\(\) => props\.cards\.map\(\(card\) => card\.id\)\);/,
  "the card loop iterates the cards' own ids");
assert.match(dashboardSource, /<For each=\{cardIds\(\)\}>\s*\{\(cardId, cardIndex\) =>/,
  "each card's demand is keyed by the id its loop item carries");
assert.match(dashboardSource, /useFrontendVisibilityDemand/);
assert.match(dashboardSource, /frontendVisibilitySurface/);
assert.doesNotMatch(dashboardSource, /setInterval|IntersectionObserver/,
  "cards must use the existing visibility controller");

assert.doesNotMatch(
  readSource("features/settings/SettingsPage.tsx"),
  /useFrontendVisibilityDemand|FrontendVisibilityDemandBinding|frontendVisibilitySurface/,
  "settings sections with no continuous work must not register empty visibility owners");
// 设置页里读后端数据的分区各自登记可见区域（分区的根元素），分区里的数据源按它取得订阅：
// 常驻页隐藏时退掉，回来时重读；不按窗口可见或常开。
for (const [section, workId, consumers] of [
  ["HardwareControlSettings", "settingsHardwareControl", ["ControlAccessLevelSetting"]],
  ["MemoryProtectionSettingsSection", "settingsMemoryProtection", ["MemoryProtectionSettingsSection"]],
  ["ReportRuleSettings", "settingsReportRules", ["ReportRuleSettings"]],
  ["PerformanceOverlaySettingsSection", "settingsPerformanceOverlay", ["PerformanceOverlaySettingsSection"]],
  ["SystemIntegrationSettingsSection", "settingsSystemIntegration", ["FrontendStartupSetting"]],
  ["PublicServicesSettingsSection", "settingsPublicServices", ["AiGatewayKeyManager"]],
  ["TrustedAccessManager", "settingsTrustedAccess", ["TrustedAccessManager"]],
  ["UpdatesSettingsSection", "settingsUpdates", ["UpdatesSettingsSection"]]
] as const) {
  const sectionSource = readSource(`features/settings/components/${section}.tsx`);
  assert.match(sectionSource, new RegExp(String.raw`const workId = frontendWorkIds\.${workId};\s*useFrontendVisibilityDemand\(workId, \[workId\]\)`),
    `${section} registers the ${workId} visible area`);
  assert.match(sectionSource, /\{\.\.\.frontendVisibilitySurface\(workId, \[workId\]\)\}/, `${section} marks its root as the ${workId} surface`);
  for (const consumer of consumers) {
    const consumerSource = readSource(`features/settings/components/${consumer}.tsx`);
    assert.match(consumerSource, new RegExp(String.raw`isNeeded\((?:workId|frontendWorkIds\.${workId})\)`), `${consumer} follows ${workId}`);
    assert.doesNotMatch(consumerSource, /frontendVisible\(\)|active: true\b/, `${consumer} does not follow window visibility alone`);
  }
}

const refreshSchedulerSource = readSource("app/useRuntimeReadContext.ts");
assert.match(
  refreshSchedulerSource,
  /usePushSource\(options\.localSystemSource,/,
  "local system status must be one independent backend current-value subscription");
assert.match(
  refreshSchedulerSource,
  /active: performanceSettingsAvailable\(\) && frontendVisible\(\)/,
  "configured subscriptions must not start with a provisional pre-settings frequency");
assert.doesNotMatch(
  refreshSchedulerSource,
  /window\.setInterval|schedulerTickMs|lastRunAt|shouldRun\(|\.refresh\(|useSource\(|ObservationState/,
  "page data must not regain frontend polling or continuous-value state machines");
assert.doesNotMatch(refreshSchedulerSource, /scheduleVisiblePageRefresh|refreshVisiblePageData|activePage/);
// 隐藏即暂停：本机状态订阅和其他数据源一样按 frontendVisible() 停止，没有按设置分支的“隐藏也刷新”。
assert.match(
  refreshSchedulerSource,
  /usePushSource\(options\.localSystemSource,[\s\S]*?active: performanceSettingsAvailable\(\) && frontendVisible\(\)/,
  "local system status must stop with the hidden window like every other source");
assert.doesNotMatch(
  refreshSchedulerSource,
  /frontendRefreshPaused|frontendHiddenRefreshMode|optimizationMode/,
  "hidden-window refresh must not depend on a setting or the optimization mode");

// 用户裁定删除的死设置不能在前端模型、设置页、文案或样式里留下读取方或开关。
for (const relativePath of [
  "types.ts",
  "App.tsx",
  "stores/settingsStore.ts",
  "app/useRuntimeReadContext.ts",
  "features/settings/SettingsPage.tsx",
  "features/settings/SettingsWorkspace.tsx",
  "features/settings/components/RuntimePolicySettingsSection.tsx",
  "features/settings/components/MemorySettingsSection.tsx",
  "features/settings/components/RefreshCadenceSetting.tsx",
  "features/settings/components/AppearanceSettingsSection.tsx",
  "i18n/settingsTypes.ts",
  "i18n/settingsLoader.ts",
  "i18n/settingsLocaleFactory.ts",
  "styles/motion.css",
  "styles/resources.css"
]) {
  assert.doesNotMatch(
    readSource(relativePath),
    /managementRefreshIntervalMs|discoveryRefreshIntervalMs|optimizationRefreshIntervalMs|frontendHiddenRefreshMode|pauseFrontendRefreshWhenHiddenInNormalMode|resourceBarHardwareAcceleration|resource-bar-hardware-acceleration|monitoringIdleSeconds|resolveAdaptiveBooleanMode/,
    `${relativePath} must not retain retired settings`);
}

const monitorStoreSource = readSource("stores/monitorStore.ts");
assert.match(monitorStoreSource, /usePushSource\(options\.resourceMonitorSource,/);
assert.match(monitorStoreSource, /usePushSource\(options\.metricSnapshotSource,/);
assert.doesNotMatch(
  monitorStoreSource,
  /resourceMonitorDemand|monitorResourceBarsNeeded|monitorResourceTableNeeded|monitorSnapshotNeeded|paused/);

const appSource = readSource("App.tsx");
assert.match(appSource, /frontendWork\.isNeeded\(frontendWorkIds\.monitorMetrics\)/);
assert.match(appSource, /frontendWork\.isNeeded\(frontendWorkIds\.monitorBars\)/);
assert.match(appSource, /frontendWork\.isNeeded\(frontendWorkIds\.monitorTable\)/);
assert.match(monitorStoreSource, /options\.resourceTableActive\(\)\s*&& resourceTableMode\(\) !== "performance"/);
// 监控和资源表按读取上下文给出的间隔订阅：间隔只按用户的刷新设置解析，没有按程序档位或窗口状态切换的第二套。
assert.match(appSource, /readContext\.readIntervals\(\)\?\.monitor/);
assert.match(appSource, /readContext\.readIntervals\(\)\?\.resourceTable/);

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
