import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";

const readSource = (relativePath: string) =>
  readFileSync(new URL(`../src/${relativePath}`, import.meta.url), "utf8");

const runtime = readSource("frontendRuntime/FrontendRuntime.ts");
const sources = readSource("frontendRuntime/source/FrontendSources.ts");
const subscriptionChannel = readSource(
  "frontendRuntime/push/BackendSubscriptionChannel.ts");
const pushValueSources = readSource(
  "frontendRuntime/push/PushValueSourceFamily.ts");
const scheduler = readSource("app/useRuntimeReadContext.ts");
const deviceStore = readSource("features/deviceTopology/deviceTopologyStore.ts");
const deviceApi = readSource("features/deviceTopology/deviceTopologyApi.ts");
const sourceRegistry = readSource("frontendRuntime/source/SourceRegistry.ts");
const sourceDescriptor = readSource("frontendRuntime/source/SourceDescriptor.ts");
const settingsStore = readSource("stores/settingsStore.ts");
const optimizationStore = readSource("stores/optimizationStore.ts");
const monitorStore = readSource("stores/monitorStore.ts");
const app = readSource("App.tsx");

assert.equal((runtime.match(/new SourceRegistry\(/g) ?? []).length, 1);
assert.equal((runtime.match(/new BackendSubscriptionChannel\(/g) ?? []).length, 1);
assert.match(
  runtime,
  /subscriptionChannel\.dispose\(\)[\s\S]*sourceRegistry\.dispose\(\)[\s\S]*backendSession\.dispose\(\)/);
assert.match(runtime, /sourceRegistry\.dispose\(\)[\s\S]*backendSession\.dispose\(\)/);
assert.match(sources, /subscriptionChannel:\s*BackendSubscriptionChannel/);
assert.equal((sources.match(/channel:\s*subscriptionChannel/g) ?? []).length, 21);
// Crash history joins the existing push channel; the frontend neither scans nor polls for crashes.
assert.match(sources, /crashReports:\s*new BackendCurrentValueSource<CrashReportList>/);
assert.match(sources, /key:\s*"crash-reports\.list"/);
assert.match(sources, /buildUrl:\s*\(\) => crashReportsSubscriptionPath/);
// 内核泄漏报告只走统一订阅通道的推送（后端在报告变化时推送，没有采样间隔）。
assert.match(sources, /kernelLeakReport:\s*new BackendCurrentValueSource/);
assert.match(sources, /key:\s*"kernel-leaks\.report"/);
// 主机内存保护的状态、记录和正常运行点只走一个订阅（/api/memory-protection/subscribe），由一个来源合成，不轮询 GET。
const memoryProtectionSource = readSource("data/memoryProtection/memoryProtectionSubscriptionSource.ts");
assert.match(sources, /memoryProtection:\s*createMemoryProtectionSubscriptionSource\(subscriptionChannel\)/);
assert.doesNotMatch(sources, /normalRunningPoint|getNormalRunningPoint/);
assert.match(memoryProtectionSource, /new FrameFoldSource</);
assert.match(memoryProtectionSource, /frames:\s*new BackendCurrentValueSource\(\{[\s\S]*channel,[\s\S]*buildUrl:\s*buildMemoryProtectionSubscriptionUrl,[\s\S]*decoder:\s*memoryProtectionFrameDecoder/);
// 产品更新状态只走统一订阅通道的推送（设置页更新区与界面内新版本提示共用）。
assert.match(sources, /productUpdateStatus:\s*new BackendCurrentValueSource/);
assert.match(sources, /key:\s*"updates\.product\.status"/);
assert.match(sources, /key:\s*"resource-monitor\.process-details"/);
assert.match(sources, /managementInventoryRevision:\s*new BackendCurrentValueSource/);
assert.match(sources, /key:\s*"management\.inventory-revision"/);
assert.match(sources, /accessRequests:\s*new BackendCurrentValueSource/);
assert.match(sources, /key:\s*"access-requests\.pending"/);
assert.match(sources, /buildUrl:\s*\(\) => buildAccessRequestPendingSubscriptionUrl\(\)/);
assert.match(pushValueSources, /this\.channel\.subscribe\(/);
assert.doesNotMatch(pushValueSources, /BackendSessionOwner|\bfetch\s*\(/);
assert.match(subscriptionChannel, /const subscriptionEndpoint = "\/api\/subscriptions\/stream"/);
assert.equal((subscriptionChannel.match(/this\.fetch\(/g) ?? []).length, 1);
assert.match(sources, /localSystemStatus:\s*new BackendPushValueSource/);
// 控制面的实际状态也走统一订阅源，和别的读数同一条通道。
assert.match(sources, /controlActualState:\s*new BackendPushValueSource/);
assert.match(sources, /deviceTopology:\s*new BackendPushValueSource/);
// 卷可用空间、手机电量等 Sampled 设备信息字段不在拓扑结构帧里，按字段选择走自己的推送源（/api/device-topology/fields/subscribe 的订阅项带 fields）。
assert.match(sources, /deviceTopologyFields:\s*new BackendPushValueSourceFamily<readonly SubscriptionFieldReference\[\], DeviceTopologyInformationSnapshot>\(\{[\s\S]*?buildUrl:\s*\(_fields, intervalMs\) => buildDeviceTopologyFieldsSubscriptionUrl\(intervalMs\),\s*buildFields:\s*normalizeInformationFieldSelection,[\s\S]*?decoder:\s*deviceTopologyInformationDecoder/);
assert.match(sources, /cpuTopology:\s*new BackendPushValueSource/);
assert.match(sources, /cpuResidency:\s*new BackendPushValueSource/);
assert.match(sources, /smartCoordinatorState:\s*new BackendPushValueSource/);
assert.doesNotMatch(sources, /retryPolicy/);
assert.match(sources, /runtimeCapabilities:\s*sourceRegistry\.define/);
assert.match(sources, /key:\s*"runtime\.capabilities"[\s\S]*canRetainStale:\s*\(\) => false/);
assert.match(sources, /appSettings:\s*sourceRegistry\.define/);
assert.match(sources, /aiGatewayCredentials:\s*sourceRegistry\.define/);
const gatewayManager = readSource("features/settings/components/AiGatewayKeyManager.tsx");
assert.match(gatewayManager, /useSource\(useFrontendRuntime\(\)\.sources\.aiGatewayCredentials/);
assert.doesNotMatch(gatewayManager, /createResource|getAiGatewayCredentials/);
assert.doesNotMatch(readSource("api.ts"), /function getAiGatewayCredentials/);
assert.match(sources, /key:\s*"app\.settings"[\s\S]*getAppSettings\(requestClient, signal\)/);
assert.match(sources, /metricSnapshot:\s*new BackendPushValueSourceFamily/);
assert.match(sources, /resourceMonitor:\s*new BackendPushValueSourceFamily/);
assert.match(sources, /gpuSpecializedTelemetry:\s*new BackendPushValueSourceFamily/);
// 规范字段的设备目录和设备值同样走统一订阅通道。
assert.match(sources, /metricFieldCatalog:\s*new BackendPushValueSource</);
assert.match(sources, /metricFields:\s*new BackendPushValueSourceFamily/);
assert.match(monitorStore, /usePushSource\(options\.metricFieldCatalogSource/);
assert.match(monitorStore, /usePushSource\(options\.metricFieldsSource/);
assert.doesNotMatch(sources, /getMetricSnapshotSource|getResourceMonitorState/);
assert.match(monitorStore, /usePushSource\(options\.metricSnapshotSource/);
assert.match(monitorStore, /usePushSource\(options\.resourceMonitorSource/);
assert.doesNotMatch(
  monitorStore,
  /SourceLeaseBinding|metricSnapshotSourceSnapshot|resourceMonitorSourceSnapshot|resourceMonitorDemand|monitorSnapshotNeeded/);
assert.doesNotMatch(monitorStore, /resourceMonitorQueryHandoff|reconcile\(/);
assert.match(sourceRegistry, /record\.descriptor\.retainLastGood === true/);
assert.match(sources, /appSettings:[\s\S]*?retainLastGood:\s*true/);
assert.match(sources, /metricCatalog:[\s\S]*?retainLastGood:\s*false/);
assert.match(sources, /optimizationReports:[\s\S]*?retainLastGood:\s*true/);
const resourceMonitorSource = sources.slice(
  sources.indexOf("resourceMonitor: new BackendPushValueSourceFamily"),
  sources.indexOf("gpuSpecializedTelemetry: new BackendPushValueSourceFamily"));
assert.doesNotMatch(resourceMonitorSource, /retainLastGood|retryPolicy|refreshInterval/);
const specializedGpuSource = sources.slice(
  sources.indexOf("gpuSpecializedTelemetry: new BackendPushValueSourceFamily"),
  sources.indexOf("gpuSchedulingModel: sourceRegistry.defineFamily"));
assert.doesNotMatch(specializedGpuSource, /retainLastGood|retryPolicy|refreshInterval/);
assert.doesNotMatch(optimizationStore, /reportSource\.acceptAuthoritative/);
// 优化页的调度状态跟随后端推送；可见时订阅，刷新按钮同时重新读取调度状态。
assert.match(sources, /smartCoordinatorStatus:\s*new BackendPushValueSource/);
assert.match(optimizationStore, /usePushSource\(options\.statusSource,/);
assert.match(app, /statusSource:\s*frontendRuntime\.sources\.smartCoordinatorStatus/);
assert.match(
  app,
  /statusActive:\s*\(\)\s*=>\s*frontendWork\.isNeeded\(frontendWorkIds\.optimizationSmartStatus\)/);
// 报告列表可见时按后端评估周期重读，不停在进页时的值。
assert.match(
  app,
  /reportsActive:\s*\(\)\s*=>\s*frontendWork\.isNeeded\(frontendWorkIds\.optimizationReports\)/);
assert.match(optimizationStore, /active: options\.enabled\(\) && options\.reportsActive\(\)/);
assert.match(
  readSource("features/optimization/OptimizationWorkspace.tsx"),
  /onRefresh=\{\(\) => \{[\s\S]*refreshReports\(true\)[\s\S]*refreshHostManagerStatus\(\)/);

assert.match(scheduler, /usePushSource\(options\.localSystemSource/);
assert.doesNotMatch(
  scheduler,
  /useSource\(|SourceSnapshot|\.refresh\(|setInterval\(|localSystemObservation/);
assert.doesNotMatch(scheduler, /getLocalSystemStatus|refreshLocalSystemStatus/);

assert.match(deviceStore, /usePushSource\(runtime\.sources\.deviceTopology/);
assert.match(deviceStore, /frontendWork\.isNeeded\(frontendWorkIds\.detailsDeviceTopology\)/);
assert.doesNotMatch(deviceStore, /useSource\(|refreshIntervalMs|setInterval\s*\(/);
assert.doesNotMatch(deviceStore, /getDeviceTopologyState|\.refresh\(/);
assert.doesNotMatch(deviceApi, /getJson</);

assert.match(sourceRegistry, /defineFamily<TQuery, TValue>/);
assert.match(sourceRegistry, /canonicalizeSourceQuery/);
assert.match(sourceRegistry, /familyRegistryKey\(familyKey, queryKey\)/);
assert.match(sourceRegistry, /record\.generation === token/);
assert.match(sourceDescriptor, /interface SourceFamilyDescriptor<TQuery, TValue>/);
assert.match(sourceDescriptor, /interface SourceFamily<TQuery, TValue>/);

assert.match(settingsStore, /options\.source\.acquire/);
assert.match(settingsStore, /source\.acceptAuthoritative\(result\)/);
assert.doesNotMatch(settingsStore, /from\s+["']\.\.\/api["']/);
assert.match(app, /source:\s*frontendRuntime\.sources\.appSettings/);
assert.doesNotMatch(scheduler, /refreshSettings:\s*\(\)\s*=>\s*settings\.refresh/);
assert.doesNotMatch(optimizationStore, /refreshSettings|getCurrentOptimizationMode/);
assert.doesNotMatch(app, /getCurrentOptimizationMode|refreshSettings:\s*settings\.refresh/);
// 隐藏即暂停后，刷新调度不再看优化模式；优化模式只由优化存储持有，App 也不再转交给调度器。
assert.doesNotMatch(scheduler, /optimizationMode/);

assert.equal(existsSync(new URL("../src/app/usePageRefreshScheduler.ts", import.meta.url)), false);
assert.equal(existsSync(new URL("../src/features/management/managementSnapshotCache.ts", import.meta.url)), false);
assert.doesNotMatch(scheduler, /activePage|management|optimization|setTimeout|requestAnimationFrame/);
const inventory = readSource("features/management/useManagementInventory.ts");
assert.equal((inventory.match(/useSource\(/g) ?? []).length, 3);
assert.doesNotMatch(readSource("stores/managementStore.ts"), /getComponents|getSoftware|RefreshPromise|RefreshPending|SnapshotCache/);
for (const name of ["Components", "Software", "BrowserRuntimes"]) {
  assert.match(sources, new RegExp("management" + name + ": sourceRegistry\\.define\\([\\s\\S]*?retainLastGood: false"));
}
