import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const readSource = (relativePath: string) =>
  readFileSync(new URL(`../src/${relativePath}`, import.meta.url), "utf8");

const runtime = readSource("frontendRuntime/FrontendRuntime.ts");
const sources = readSource("frontendRuntime/source/FrontendSources.ts");
const subscriptionChannel = readSource(
  "frontendRuntime/push/BackendSubscriptionChannel.ts");
const pushValueSources = readSource(
  "frontendRuntime/push/PushValueSourceFamily.ts");
const scheduler = readSource("app/usePageRefreshScheduler.ts");
const deviceStore = readSource("deviceTopology/deviceTopologyStore.ts");
const deviceApi = readSource("deviceTopology/deviceTopologyApi.ts");
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
assert.equal((sources.match(/channel:\s*subscriptionChannel/g) ?? []).length, 10);
assert.match(pushValueSources, /this\.channel\.subscribe\(/);
assert.doesNotMatch(pushValueSources, /BackendSessionOwner|\bfetch\s*\(/);
assert.match(subscriptionChannel, /const subscriptionEndpoint = "\/api\/subscriptions\/stream"/);
assert.equal((subscriptionChannel.match(/this\.fetch\(/g) ?? []).length, 1);
assert.match(sources, /localSystemStatus:\s*new BackendPushValueSource/);
assert.match(sources, /deviceTopology:\s*new BackendPushValueSource/);
assert.match(sources, /cpuTopology:\s*new BackendPushValueSource/);
assert.match(sources, /cpuResidency:\s*new BackendPushValueSource/);
assert.match(sources, /smartCoordinatorState:\s*new BackendPushValueSource/);
assert.doesNotMatch(sources, /retryPolicy/);
assert.match(sources, /runtimeCapabilities:\s*sourceRegistry\.define/);
assert.match(sources, /key:\s*"runtime\.capabilities"[\s\S]*canRetainStale:\s*\(\) => false/);
assert.match(sources, /selfScheduling:\s*new BackendPushValueSource/);
assert.match(sources, /key:\s*"resource-manager\.self-scheduling"/);
assert.match(sources, /appSettings:\s*sourceRegistry\.define/);
assert.match(sources, /key:\s*"app\.settings"[\s\S]*getAppSettings\(requestClient, signal\)/);
assert.match(sources, /metricSnapshot:\s*new BackendPushValueSourceFamily/);
assert.match(sources, /resourceMonitor:\s*new BackendPushValueSourceFamily/);
assert.match(sources, /gpuSpecializedTelemetry:\s*new BackendPushValueSourceFamily/);
assert.doesNotMatch(sources, /getMetricSnapshotSource|getResourceMonitorState/);
assert.match(monitorStore, /options\.metricSnapshotSource\.subscribe/);
assert.match(monitorStore, /options\.resourceMonitorSource\.subscribe/);
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

assert.match(scheduler, /options\.localSystemSource\.subscribe/);
assert.match(scheduler, /options\.selfSchedulingSource\.subscribe/);
assert.doesNotMatch(
  scheduler,
  /useSource\(|SourceSnapshot|\.refresh\(|setInterval\(|selfSchedulingObservation|localSystemObservation/);
assert.doesNotMatch(scheduler, /getLocalSystemStatus|refreshLocalSystemStatus/);
assert.doesNotMatch(scheduler, /getResourceManagerSelfScheduling|refreshSelfScheduling/);

assert.match(deviceStore, /runtime\.sources\.deviceTopology\.subscribe/);
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
assert.match(app, /optimizationMode:\s*optimization\.optimizationMode/);
