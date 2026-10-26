import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const readSource = (relativePath: string) =>
  readFileSync(new URL(`../src/${relativePath}`, import.meta.url), "utf8");
const readAppSource = (relativePath: string) =>
  readFileSync(new URL(`../../../../src/Core/${relativePath}`, import.meta.url), "utf8");

const capabilities = readSource("stores/runtimeCapabilitiesStore.ts");
const app = readSource("App.tsx");
const frontendSources = readSource("frontendRuntime/source/FrontendSources.ts");
const appShell = readSource("app/AppShell.tsx");
const details = readSource("features/details/DetailsPage.tsx");
const detailsNavigation = readSource("app/navigation/detailsNavigation.ts");
const managementWorkspace = readSource("features/management/ManagementWorkspace.tsx");
const managementPage = readSource("features/management/components/ManagementPage.tsx");
const settingsWorkspace = readSource("features/settings/SettingsWorkspace.tsx");
const settingsPage = readSource("features/settings/SettingsPage.tsx");
const appearanceSettings = readSource("features/settings/components/AppearanceSettingsSection.tsx");
const systemIntegrationSettings = readSource("features/settings/components/SystemIntegrationSettingsSection.tsx");
const resourceTable = readSource("features/resourceTable/ResourceTable.tsx");
const resourceBreakdown = readSource("features/resourceBreakdown/ResourceBreakdown.tsx");
const resourcePaintPlane = readSource("features/resourceBreakdown/ResourceSegmentPaintPlane.tsx");
const monitorPage = readSource("features/monitor/MonitorPage.tsx");
const monitorStore = readSource("stores/monitorStore.ts");
const deviceStore = readSource("features/deviceTopology/deviceTopologyStore.ts");
const deviceObservation = readSource("features/deviceTopology/deviceTopologyObservation.ts");
const deviceView = readSource("features/deviceTopology/DeviceTopologyView.tsx");
const smartReport = readSource("features/details/HostManagerSmartCoordinatorDetailsReport.tsx");
const gpuModel = readSource("features/gpuScheduling/GpuSchedulingModel.tsx");
const cpuModel = readSource("features/details/CpuTopologyDiagram.tsx");
const api = readSource("api.ts");
const httpTransport = readSource("api/httpTransport.ts");
const adapterEndpoints = readAppSource("Endpoints/AdapterEndpoints.cs");
const frontendStateEndpoints = readAppSource("Endpoints/FrontendRuntimeStateEndpoints.cs");
const endpointMap = readAppSource("Endpoints/ResourceManagerEndpointRouteBuilderExtensions.cs");

// Capability discovery remains fail-closed, but a failed request must be visible and retryable.
assert.match(capabilities, /useSource\(handle/);
assert.match(capabilities, /failClosedCapabilities/);
assert.match(capabilities, /sourceCanRender\(snapshot\)/);
assert.match(capabilities, /failedObservation\(/);
assert.doesNotMatch(capabilities, /getRuntimeCapabilities|createSignal|setCurrent/);
assert.match(frontendSources, /runtimeCapabilities:\s*sourceRegistry\.define/);
assert.match(frontendSources, /canRetainStale:\s*\(\) => false/);
assert.match(frontendSources, /selfScheduling:\s*new BackendPushValueSource/);
assert.match(frontendSources, /localSystemStatus:\s*new BackendPushValueSource/);
assert.doesNotMatch(appShell, /selfSchedulingObservation|localSystemObservation|自调度状态|运行时长不可用/);
assert.match(appShell, /label=\{uiText\.shell\.runtimeCapability\}/);
assert.match(appShell, /runtimeCapabilities\.refresh\(\)/);

// Read-only detail surfaces stay available while effectful reports and edits follow capabilities.
// 详细信息的二级分页清单在分页树里（app/navigation/detailsNavigation.ts）：报告跟着优化能力走。
assert.match(detailsNavigation, /id !== "report" \|\| options\.optimizationEnabled\(\)/);
assert.match(app, /optimizationEnabled: runtimeCapabilities\.optimizationEnabled,/);
assert.match(details, /GpuSchedulingModel runtimeEffectsEnabled=/);
assert.doesNotMatch(
  gpuModel,
  /createSignal<MetricSnapshot>\(\{[\s\S]*capturedAt:\s*null,[\s\S]*items:\s*\{\}/);
assert.match(gpuModel, /const metricSnapshot = usePushSource\(runtime\.sources\.metricSnapshot,/);
assert.match(gpuModel, /query: \{ ids: gpuIndexes\(\)\.flatMap/);
assert.match(
  gpuModel,
  /metric && sourceCanRender\(metric\)[\s\S]*resource && sourceCanRender\(resource\)/);
assert.match(details, /CpuTopologyDiagram[\s\S]*runtimeEffectsEnabled=/);

// The cancelled data migration feature must expose no workspace or requests.
assert.doesNotMatch(managementWorkspace, /MigrationPanel|migrationWorkbenchPage/);

// Entity-specific actions must not route components through process/software menus.
const componentCard = managementPage.match(
  /function ComponentCard[\s\S]*?function SoftwareCard/)?.[0] ?? "";
assert.ok(componentCard, "The component card implementation must remain present.");
assert.match(componentCard, /<DetailsButton/);
assert.doesNotMatch(componentCard, /onContextMenu|MoreActionsButton|softwareContextTargetFromComponent/);

// Draft settings belong to one settings-page visit and read-only credits own no edit toolbar.
assert.match(settingsWorkspace, /onCleanup\(settings\.discardDraftChanges\)/);
assert.match(settingsPage, /settingsReady\(\) && props\.activeSection !== "credits"/);
for (const control of [
  "onAnimationsChange",
  "onFontSmoothingChange"
]) {
  assert.match(appearanceSettings, new RegExp(`onChange=\\{props\\.${control}\\}`));
  assert.match(settingsPage, new RegExp(`${control}=\\{props\\.${control}\\}`));
  assert.match(settingsWorkspace, new RegExp(`${control}=\\{settings\\.update`));
}
assert.match(settingsWorkspace, /onBarColorModeChange=\{settings\.updateBarColorMode\}/);
assert.match(settingsPage, /onBarColorModeChange=\{props\.onBarColorModeChange\}/);
assert.match(appearanceSettings, /value=\{appearance\(\)\.barColorMode \?\? pointPreferences\(\)\.appearance\.barColorMode!\}/);
assert.match(appearanceSettings, /options=\{props\.text\.barColorOptions\}/);
assert.match(appearanceSettings, /onChange=\{props\.onBarColorModeChange\}/);
assert.match(app, /document\.body\.dataset\.barColor = normalizeBarColorMode\(settings\.settings\(\)\.appearance\?\.barColorMode\)/);
// The settings owner resolves Auto from focus and GPU scheduling into data-animations.
assert.match(resourcePaintPlane, /attributeFilter: \["data-animations", "data-bar-color", "data-resource-bar-frame-rate"\]/);
assert.match(resourcePaintPlane, /presentation\.barColor === "distinct"\s*\? segment\.distinctColor \?\? segment\.color\s*: segment\.color/);
// 两种配色都画分隔线（异色区分时相邻两种颜色也要分开），不按配色关掉。
assert.doesNotMatch(resourcePaintPlane, /barColor !== "distinct"/);
assert.match(systemIntegrationSettings, /uiText\.misc\.publicServicesNotRunning/);

// Monitor actions and copy must reflect actual available behavior.
assert.match(resourceTable, /<Show when=\{props\.mode !== "performance"\}>[\s\S]*?onClick=\{props\.onToggleEdit\}/);
// 表头措辞由前端按列 id 和列的来源出；后端只发 id，所以不能再有任何一处直接印 column.label。
assert.match(resourceTable, /<span>\{columnText\(column\(\)\)\.label\}<\/span>/);
assert.doesNotMatch(resourceTable, /column\.label|column\(\)\.label/);
assert.doesNotMatch(resourceBreakdown, /采样已经完成，但所选指标暂时没有可归属的资源/);
assert.doesNotMatch(resourceBreakdown, /<For each=\{barKeys\(\)\}>/);
assert.doesNotMatch(resourceBreakdown, /<For each=\{keys\(\)\}>/);
assert.equal(
  (resourceBreakdown.match(/resource-segment-interaction-layer/g) ?? []).length,
  2,
  "Software and process tracks must each own one bounded interaction layer."
);
assert.match(resourceBreakdown, /aria-posinset=\{item\(\)\.index \+ 1\}/);
assert.match(resourceBreakdown, /aria-setsize=\{layout\(\)\.length\}/);
assert.match(resourcePaintPlane, /drawSegmentLabels\(/);
assert.match(resourcePaintPlane, /fitCanvasLabel\(/);
assert.match(monitorStore, /if \(mode === "performance"\) \{\s*cancelResourceTableEdit\(\)/);
assert.match(monitorStore, /if \(resourceTableMode\(\) === "performance"\) \{\s*return;/);
// 性能面板只画目录里可选的曲线，读不到的项不画空线。
assert.match(readSource("features/resourceTable/ResourcePerformancePanel.tsx"),
  /\.filter\(\(id\) => available\.has\(id\) && available\.get\(id\)!\.selectable !== false\)/);
// 目录订阅帧（{catalogRevision, fields, metrics}）收到后由它独自写指标目录：同帧生效、按内容替换（同一修订号下
// 组件安装信息也会变），修订号变化时仍是出厂布局就重取看板设置；GET 结果和快照帧的修订号不再改目录。
assert.match(monitorStore, /function applyCatalogPush\(push: MetricCatalogPush\): void \{\s*catalogFromPush = true;/);
assert.match(monitorStore, /if \(JSON\.stringify\(catalog\(\)\) !== JSON\.stringify\(push\.metrics\)\) setCatalog\(push\.metrics\);/);
assert.match(monitorStore, /if \(push\.catalogRevision !== backendCatalogRevision\) \{\s*backendCatalogRevision = push\.catalogRevision;\s*if \(dashboardUsesFactoryDefaults\) void refreshSettings\(\);/);
assert.match(monitorStore, /source: SourceSnapshot<MetricDefinition\[\]>\s*\): void \{\s*if \(catalogFromPush\) \{\s*return;/);
assert.match(monitorStore, /on\(fieldCatalogSnapshot, value => \{[\s\S]*?options\.metricSnapshotActive\(\) && options\.metricSnapshotIntervalMs\(\) !== null && value\.data\) \{\s*applyCatalogPush\(value\.data\);/);
// 订阅没收到帧时（或没有订阅），快照帧的目录修订号第一次出现或变化时重取目录；还是出厂布局时再重取看板设置。
assert.match(monitorStore, /dashboardUsesFactoryDefaults = settings\.usesFactoryDefaults === true;/);
assert.match(monitorStore, /if \(value\.data\) \{[\s\S]*?if \(!catalogFromPush && value\.data\.catalogRevision !== backendCatalogRevision\) \{\s*backendCatalogRevision = value\.data\.catalogRevision;\s*void followBackendCatalog\(value\.data\.catalogRevision\);/);
assert.match(monitorStore, /if \(!await refreshCatalog\(\)\) \{\s*if \(backendCatalogRevision === revision\) \{\s*backendCatalogRevision = null;/);
assert.match(monitorStore, /if \(dashboardUsesFactoryDefaults\) \{\s*await refreshSettings\(\);/);
// 看板布局由后端按本机目录生成，设置到达前不画前端自带的占位卡。
assert.match(monitorStore, /const \[cards, setCards\] = createSignal<DashboardCardSettings\[\]>\(\[\]\);/);
for (const retiredMonitorRefresh of [
  "refreshSnapshot",
  "refreshResourceBars",
  "refreshResourceTable",
  "refreshResourceBreakdown",
  "onRefreshData"
]) {
  assert.doesNotMatch(monitorStore, new RegExp(retiredMonitorRefresh));
  assert.doesNotMatch(monitorPage, new RegExp(retiredMonitorRefresh));
  assert.doesNotMatch(resourceTable, new RegExp(retiredMonitorRefresh));
}

// Device topology and smart reports render only values received from backend callbacks.
assert.match(deviceStore, /usePushSource\(runtime\.sources\.deviceTopology,/);
assert.doesNotMatch(deviceStore, /getDeviceTopologyState|setObservation|pollRequest|useFrontendWorkPoll/);
assert.match(deviceStore, /projectDeviceTopologyObservation/);
assert.match(deviceObservation, /state\.state === "ready"/);
assert.match(deviceObservation, /state\.state === "failed"/);
assert.match(deviceObservation, /status: "stale"/);
assert.match(deviceView, /state=\{topology\.observation\(\)\}/);
assert.match(smartReport, /usePushSource\(frontendRuntime\.sources\.smartCoordinatorState,/);
assert.doesNotMatch(
  smartReport,
  /getHostManagerRollbackState|useFrontendWorkPoll|setObservation|ObservationStateBoundary|onRetry/);

// GPU scheduling loads static scoring data once and displays three backend push streams directly.
assert.match(gpuModel, /runtime\.sources\.metricCatalog\.acquire/);
assert.match(gpuModel, /runtime\.sources\.gpuSchedulingModel\.acquire/);
assert.match(gpuModel, /modelBinding\.switch/);
assert.match(gpuModel, /usePushSource\(runtime\.sources\.metricSnapshot,/);
assert.match(gpuModel, /usePushSource\(runtime\.sources\.resourceMonitor,/);
assert.match(gpuModel, /usePushSource\(runtime\.sources\.gpuSpecializedTelemetry,/);
assert.doesNotMatch(gpuModel, /backendEpoch|useFrontendWorkPoll|sourceTracker\.run|\.latest/);
assert.match(gpuModel, /ObservationStateBoundary/);
assert.match(gpuModel, /props\.runtimeEffectsEnabled && observation\(\)\.status === "ready"/);
assert.doesNotMatch(gpuModel, /SourceLeaseHandoff|modelHandoff/);

// CPU topology and residency render only their independent backend callback values.
assert.match(cpuModel, /usePushSource\(frontendRuntime\.sources\.cpuTopology,/);
assert.match(cpuModel, /usePushSource\(frontendRuntime\.sources\.cpuResidency,/);
assert.match(cpuModel, /frontendRuntime\.sources\.cpuExclusiveBindings/);
assert.doesNotMatch(cpuModel, /getCpuTopologySource|getCpuResidencySource|refreshAll/);
assert.match(cpuModel, /exclusiveBindingsSource\.snapshot\(\)/);
assert.doesNotMatch(cpuModel, /ObservationStateBoundary|residencyStateLabel|formatResidencyState|onRetry/);
assert.match(cpuModel, /!props\.runtimeEffectsEnabled \|\| !snapshot\(\)/);
assert.match(cpuModel, /const topologySummary = createMemo\([\s\S]*?snapshot\(\)/);
assert.match(cpuModel, /<Show when=\{topologySummary\(\)\}/);

// Process-local frontend/self state has a dedicated endpoint owner; persistent adapter writes stay separate.
assert.match(endpointMap, /MapFrontendRuntimeStateEndpoints\(\)/);
assert.match(frontendStateEndpoints, /transient self-scheduling state/);
assert.match(frontendStateEndpoints, /MapPost\("\/api\/adapters\/resource-manager\/scheduling"/);
assert.doesNotMatch(frontendStateEndpoints, /visible-regions|ResourceManagerVisibleRegion/);
assert.doesNotMatch(adapterEndpoints, /resource-manager\/scheduling/);
assert.doesNotMatch(adapterEndpoints, /resource-manager\/visible-regions/);

// A hung or malformed local response must settle through the shared bounded transport.
assert.doesNotMatch(api, /\bfetch\(/);
assert.match(api, /requestJson/);
assert.match(httpTransport, /defaultApiRequestTimeoutMs/);
assert.match(httpTransport, /kind: "timeout"/);
assert.match(httpTransport, /kind: "invalid-response"/);
assert.match(httpTransport, /callerSignal\?\.addEventListener\("abort"/);
