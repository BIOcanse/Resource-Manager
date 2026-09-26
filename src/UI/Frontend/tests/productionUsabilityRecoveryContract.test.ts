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
const details = readSource("components/DetailsPage.tsx");
const migration = readSource("stores/migrationStore.ts");
const managementWorkspace = readSource("features/management/ManagementWorkspace.tsx");
const managementPage = readSource("features/management/components/ManagementPage.tsx");
const settingsWorkspace = readSource("pages/SettingsWorkspace.tsx");
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
const smartReport = readSource("components/HostManagerSmartCoordinatorDetailsReport.tsx");
const gpuModel = readSource("components/GpuSchedulingModel.tsx");
const cpuModel = readSource("components/CpuTopologyDiagram.tsx");
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
assert.match(details, /tab\.id !== "report" \|\| props\.runtimeCapabilities\.optimizationEnabled\(\)/);
assert.match(details, /GpuSchedulingModel runtimeEffectsEnabled=/);
assert.doesNotMatch(
  gpuModel,
  /createSignal<MetricSnapshot>\(\{[\s\S]*capturedAt:\s*null,[\s\S]*items:\s*\{\}/);
assert.match(gpuModel, /createSignal<MetricSnapshot \| null>\(null\)/);
assert.match(
  gpuModel,
  /data && metricSnapshot\(\) !== null && resourceBreakdown\(\) !== null/);
assert.match(details, /CpuTopologyDiagram[\s\S]*runtimeEffectsEnabled=/);

// Migration requests preserve last-good values and expose independent retryable observations.
for (const key of ["roots", "records", "sessions"] as const) {
  assert.match(migration, new RegExp(`${key}Observation`));
  assert.match(managementWorkspace, new RegExp(`migration\\.${key}Observation\\(\\)`));
  assert.match(managementWorkspace, new RegExp(`migration\\.refresh${key[0].toUpperCase()}${key.slice(1)}\\(\\)`));
}
assert.doesNotMatch(migration, /catch\s*\{\s*setRoots\(null\)/);
assert.doesNotMatch(migration, /catch\s*\{\s*setRecords\(\[\]\)/);
assert.doesNotMatch(migration, /catch\s*\{\s*setSessions\(\[\]\)/);

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
  "onResourceBarHardwareAccelerationModeChange",
  "onFontSmoothingChange"
]) {
  assert.match(appearanceSettings, new RegExp(`onChange=\\{props\\.${control}\\}`));
  assert.match(settingsPage, new RegExp(`${control}=\\{props\\.${control}\\}`));
  assert.match(settingsWorkspace, new RegExp(`${control}=\\{settings\\.update`));
}
assert.match(settingsWorkspace, /onBarColorModeChange=\{settings\.updateBarColorMode\}/);
assert.match(settingsPage, /onBarColorModeChange=\{props\.onBarColorModeChange\}/);
assert.match(appearanceSettings, /value=\{appearance\(\)\.barColorMode \?\? "type"\}/);
assert.match(appearanceSettings, /options=\{props\.text\.barColorOptions\}/);
assert.match(appearanceSettings, /onChange=\{props\.onBarColorModeChange\}/);
assert.match(app, /document\.body\.dataset\.barColor = normalizeBarColorMode\(settings\.settings\(\)\.appearance\?\.barColorMode\)/);
assert.match(resourcePaintPlane, /attributeFilter: \["data-animations", "data-bar-color", "data-self-gpu-grade", "data-frontend-focused"\]/);
assert.match(resourcePaintPlane, /presentation\.barColor === "distinct"\s*\? segment\.distinctColor \?\? segment\.color\s*: segment\.color/);
assert.match(resourcePaintPlane, /presentation\.barColor !== "distinct"/);
assert.match(systemIntegrationSettings, /uiText\.misc\.publicServicesNotRunning/);

// Monitor actions and copy must reflect actual available behavior.
assert.match(resourceTable, /<Show when=\{props\.mode !== "performance"\}>[\s\S]*?onClick=\{props\.onToggleEdit\}/);
// 表头措辞由前端按列 id 出；后端只发 id，所以不能再有任何一处直接印 column.label。
assert.match(resourceTable, /resourceTableColumnLabel\(column\(\)\.id\)/);
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
assert.match(deviceStore, /runtime\.sources\.deviceTopology\.subscribe/);
assert.doesNotMatch(deviceStore, /getDeviceTopologyState|setObservation|pollRequest|useFrontendWorkPoll/);
assert.match(deviceStore, /projectDeviceTopologyObservation/);
assert.match(deviceObservation, /state\.state === "ready"/);
assert.match(deviceObservation, /state\.state === "failed"/);
assert.match(deviceObservation, /status: "stale"/);
assert.match(deviceView, /state=\{topology\.observation\(\)\}/);
assert.match(smartReport, /sources\.smartCoordinatorState\.subscribe/);
assert.doesNotMatch(
  smartReport,
  /getHostManagerRollbackState|useFrontendWorkPoll|setObservation|ObservationStateBoundary|onRetry/);

// GPU scheduling loads static scoring data once and displays three backend push streams directly.
assert.match(gpuModel, /runtime\.sources\.metricCatalog\.acquire/);
assert.match(gpuModel, /runtime\.sources\.gpuSchedulingModel\.acquire/);
assert.match(gpuModel, /modelBinding\.switch/);
assert.match(gpuModel, /runtime\.sources\.metricSnapshot\.subscribe/);
assert.match(gpuModel, /runtime\.sources\.resourceMonitor\.subscribe/);
assert.match(gpuModel, /runtime\.sources\.gpuSpecializedTelemetry\.subscribe/);
assert.doesNotMatch(gpuModel, /backendEpoch|useFrontendWorkPoll|sourceTracker\.run|\.latest/);
assert.match(gpuModel, /ObservationStateBoundary/);
assert.match(gpuModel, /props\.runtimeEffectsEnabled && observation\(\)\.status === "ready"/);
assert.doesNotMatch(gpuModel, /SourceLeaseHandoff|modelHandoff/);

// CPU topology and residency render only their independent backend callback values.
assert.match(cpuModel, /frontendRuntime\.sources\.cpuTopology\.subscribe/);
assert.match(cpuModel, /frontendRuntime\.sources\.cpuResidency\.subscribe/);
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
