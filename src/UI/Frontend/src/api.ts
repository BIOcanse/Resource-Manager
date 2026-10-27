import { readDashboardPreferences, saveDashboardPreferences } from "./preferences/dashboardPreferences.ts";
import { sendElevatedBackendJson } from "./api/elevatedBackendRequest.ts";
import {
  expectedOverlayInjectionModules,
  overlayInjectionModules,
  overlayStartupModuleIds,
  startupModuleSelectionDecoder,
  type StartupModuleSelection
} from "./features/overlay/startupModuleSelection.ts";
import { frontendPreferencesClient, pointOverlayPresentation, cachedOverlay } from "./preferences/frontendPreferences.ts";
import type {
  BackendMessage,
  AiGatewayCompatibilityProfile,
  AiGatewayCredentialCreatedView,
  ComponentVersionOptions,
  CpuCorePerformanceOverrideRequest,
  CpuCorePerformanceOverrideResult,
  DashboardSettings,
  GpuPlacementProcessObservationRequest,
  GpuPlacementProcessPolicy,
  RuntimePolicySaveResult,
  GpuPlacementSoftwarePolicy,
  GpuPlacementSoftwareProcessHistory,
  GpuPlacementSoftwareSettingsSnapshot,
  PerformanceOverlaySettings,
  GpuPerformanceScoreOverrideRequest,
  GpuPerformanceScoreOverrideResult,
  GpuPerformanceScoreSnapshot,
  GpuSpecializedTelemetrySnapshot,
  LocalOnlineSearchResult,
  ManualSoftwareRequest,
  PortableSoftwareRootConfirmationResult,
  ResourceBarSettings,
  AppOptimizationMode,
  SystemProcessIdentity,
  SystemProcessOperationResult,
  TrustedOptimizationTarget
} from "./types";
import {
  decodeResourceBreakdownWireSnapshot,
  type ResourceBreakdownWireSnapshot
} from "./api/resourceBreakdownWire.ts";
import { decodeHostManagerRollbackState } from "./api/hostManagerRollbackWire.ts";
import { decodeHostManagerSmartCoordinatorStatus } from
  "./data/optimization/hostManagerSmartStatusSource.ts";
import { ApiRequestError, requestJson, requestNoContent } from "./api/httpTransport.ts";
import { reportRuleSettingsDecoder } from "./data/appSettings/reportRuleSettingsDecoder.ts";
import { metricCatalogDecoder } from "./data/monitor/monitorSourceDecoders.ts";
import { metricOfferedInPicker } from "./presentation/metricPresentation.ts";
import { cpuTopologyDecoder, cpuResidencyDecoder, cpuExclusiveBindingsDecoder } from "./data/cpu/cpuSourceDecoders.ts";
import { decodeRuntimePolicySaveResult } from "./api/runtimePolicySaveResultDecoder.ts";
import { decodeSoftwarePolicy } from "./data/softwarePolicy/softwarePolicyDecoder.ts";
import { uiText } from "./text.ts";
import { withProcessActionElevation } from "./processes/elevatedProcessAction.ts";

export async function getJson<T>(
  url: string,
  options: { signal?: AbortSignal; timeoutMs?: number } = {}
): Promise<T> {
  return requestJson<T>(url, {
    cache: "no-store",
    fallbackError: uiText.apiError.readFailed,
    ...options
  });
}

export async function postJson<T>(
  url: string,
  body: unknown,
  fallbackError: string,
  keepalive = false,
  options: { signal?: AbortSignal; timeoutMs?: number } = {}): Promise<T> {
  return requestJson<T>(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    keepalive,
    fallbackError,
    ...options
  });
}

export async function getReportRuleSettings(signal?: AbortSignal) {
  return reportRuleSettingsDecoder.decode(await getJson<unknown>("/api/settings/report-rules", { signal }));
}

export async function getMetricCatalog() {
  return metricCatalogDecoder.decode(await getJson<unknown>("/api/metrics/catalog"));
}

/** 叠加层设置：显示方式和外观归前端偏好；是否注入看后端登记的启动模块，读取时总是一并读出。 */
export async function getPerformanceOverlaySettings(softwareId: string): Promise<PerformanceOverlaySettings> {
  const preferences = await frontendPreferencesClient().read();
  const presentation = { ...pointOverlayPresentation(), ...cachedOverlay(preferences, softwareId) };
  const injectionModules = overlayInjectionModules(await getStartupModuleSelection(softwareId));
  if (presentation.mode === "injected") {
    presentation.enabled = injectionModules === "complete";
  }
  // 来源出现之前写的缓存没有 metricSources：点的默认值是空表，每一项都是自动选择。
  return { softwareId, injectionModules, ...presentation, metrics: [...presentation.metrics],
    metricSources: { ...presentation.metricSources } };
}

/**
 * 设置 → 性能浮层的总览：前端偏好里有叠加层设置的每个软件，按叠加层分页同一读法（getPerformanceOverlaySettings）
 * 得出是否启用，只留启用的。不读运行状态。
 */
export async function listEnabledPerformanceOverlays(): Promise<PerformanceOverlaySettings[]> {
  const preferences = await frontendPreferencesClient().read();
  const settings = await Promise.all(Object.keys(preferences.overlays).map(getPerformanceOverlaySettings));
  return settings.filter((overlay) => overlay.enabled);
}

function startupModuleSelectionPath(softwareId: string) {
  return `/api/startup-modules/software/${encodeURIComponent(softwareId)}`;
}

async function getStartupModuleSelection(softwareId: string): Promise<StartupModuleSelection> {
  return startupModuleSelectionDecoder.decode(await getJson<unknown>(startupModuleSelectionPath(softwareId)));
}

/** 启动模块已经在后端改好，但叠加层偏好没有保存成功。界面据此提示“后端已更新、偏好未保存”。 */
export class OverlayPreferencesSaveError extends Error {
  constructor(cause: unknown) {
    super("The startup modules were saved, but the overlay preferences were not.", { cause });
    this.name = "OverlayPreferencesSaveError";
  }
}

export async function getPerformanceOverlayMetricCatalog() {
  return metricCatalogDecoder.decode(await getJson<unknown>("/api/performance-overlay/metrics/catalog"))
    .filter(metricOfferedInPicker);
}

export async function savePerformanceOverlaySettings(settings: PerformanceOverlaySettings,
    previous?: Pick<PerformanceOverlaySettings, "enabled" | "mode">): Promise<PerformanceOverlaySettings> {
  const { softwareId, injectionModules: readModules, ...presentation } = settings;
  const expected = expectedOverlayInjectionModules(settings);
  let injectionModules = readModules;
  let modulesChanged = false;
  // 开关或方式改变，或后端登记与这里的选择不一致时，按这里的选择更新后端登记。
  if (!previous || previous.enabled !== settings.enabled || previous.mode !== settings.mode || readModules !== expected) {
    const path = startupModuleSelectionPath(softwareId);
    const current = await getStartupModuleSelection(softwareId);
    const overlayIds: readonly string[] = overlayStartupModuleIds;
    const unrelated = current.moduleIds.filter(id => !overlayIds.includes(id));
    const moduleIds = [...unrelated, ...(expected === "complete" ? overlayStartupModuleIds : [])];
    if (JSON.stringify([...current.moduleIds].sort()) !== JSON.stringify([...moduleIds].sort())) {
      await sendElevatedBackendJson<StartupModuleSelection>("PUT", path, { softwareId, moduleIds }, uiText.apiError.saveGpuSoftwarePolicyFailed);
      modulesChanged = true;
    }
    injectionModules = expected;
  }
  const owner = frontendPreferencesClient();
  try {
    const preferences = await owner.read();
    await owner.save({ ...preferences, overlays: { ...preferences.overlays, [softwareId]: presentation } }, { overlay: softwareId });
  } catch (error) {
    throw modulesChanged ? new OverlayPreferencesSaveError(error) : error;
  }
  return { softwareId, injectionModules, ...presentation, metrics: [...presentation.metrics],
    metricSources: { ...presentation.metricSources } };
}

export async function resetPerformanceOverlayPresentation(softwareId: string) {
  const owner = frontendPreferencesClient();
  const preferences = await owner.read();
  const overlays = { ...preferences.overlays };
  const current = cachedOverlay(preferences, softwareId) ?? pointOverlayPresentation();
  const presentation = { ...pointOverlayPresentation(), enabled: current.enabled, mode: current.mode };
  for (const id of Object.keys(overlays)) if (id.toLowerCase() === softwareId.toLowerCase()) delete overlays[id];
  overlays[softwareId] = presentation;
  await owner.save({ ...preferences, overlays }, { overlay: softwareId });
  return presentation;
}

export async function getDashboardSettings() {
  return readDashboardPreferences(await getMetricCatalog());
}

export async function saveDashboardSettings(settings: DashboardSettings) {
  return saveDashboardPreferences(settings);
}
export async function createAiGatewayCredential(
  compatibilityProfile: AiGatewayCompatibilityProfile,
  displayName: string)
{
  return postJson<AiGatewayCredentialCreatedView>(
    "/api/ai-gateway/credentials",
    { compatibilityProfile, displayName },
    uiText.apiError.generateAiKeyFailed);
}

export async function revokeAiGatewayCredential(credentialId: string) {
  return deleteJson<{ revoked: boolean }>(
    `/api/ai-gateway/credentials/${encodeURIComponent(credentialId)}`,
    uiText.apiError.revokeAiKeyFailed);
}

export async function getGpuSpecializedTelemetry(counterIds: string[] | null = null) {
  if (counterIds === null) {
    return getJson<GpuSpecializedTelemetrySnapshot>("/api/metrics/gpu-specialized");
  }

  const params = new URLSearchParams();
  counterIds.forEach((id) => params.append("ids", id));
  return getJson<GpuSpecializedTelemetrySnapshot>(`/api/metrics/gpu-specialized?${params.toString()}`);
}

export async function getCpuTopology() {
  return cpuTopologyDecoder.decode(await getJson<unknown>("/api/cpu/topology"));
}

export async function getCpuResidency() {
  return cpuResidencyDecoder.decode(await getJson<unknown>("/api/cpu/residency"));
}

export async function getCpuExclusiveBindings() {
  return cpuExclusiveBindingsDecoder.decode(await getJson<unknown>("/api/cpu/topology/exclusive-bindings"));
}

export async function saveCpuCorePerformanceOverrides(request: CpuCorePerformanceOverrideRequest) {
  return saveRuntimePolicy<CpuCorePerformanceOverrideResult>(
    "/api/cpu/topology/performance-overrides",
    request,
    uiText.apiError.saveCpuCoreScoreFailed);
}

export async function resetCpuCorePerformanceOverrides(cpuName: string) {
  const params = new URLSearchParams();
  params.set("cpuName", cpuName);
  return resetRuntimePolicy<CpuCorePerformanceOverrideResult>(
    `/api/cpu/topology/performance-overrides?${params.toString()}`,
    uiText.apiError.resetCpuCoreScoreFailed);
}

export async function getGpuPerformanceScoreOverrides() {
  return getJson<GpuPerformanceScoreOverrideResult>("/api/gpu/performance-overrides");
}

export async function getGpuPerformanceScores() {
  return getJson<GpuPerformanceScoreSnapshot>("/api/gpu/performance-scores");
}

export async function saveGpuPerformanceScoreOverrides(request: GpuPerformanceScoreOverrideRequest) {
  return saveRuntimePolicy<GpuPerformanceScoreOverrideResult>(
    "/api/gpu/performance-overrides",
    request,
    uiText.apiError.saveGpuScoreFailed);
}

export async function resetGpuPerformanceScoreOverrides() {
  return resetRuntimePolicy<GpuPerformanceScoreOverrideResult>(
    "/api/gpu/performance-overrides",
    uiText.apiError.resetGpuScoreFailed);
}

export async function putJson<T>(url: string, body: unknown, fallbackError: string): Promise<T> {
  return requestJson<T>(url, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    fallbackError
  });
}

export async function deleteJson<T>(url: string, fallbackError: string): Promise<T> {
  return requestJson<T>(url, { method: "DELETE", fallbackError });
}

export async function searchOnline(query: string) {
  return postJson<LocalOnlineSearchResult>("/api/system/search-online", { query }, uiText.apiError.onlineSearchFailed);
}

/** 版本对话框的数据源：已验证版本 + 最新版本。最新版本解析失败时该项带原因返回。 */
export async function fetchComponentVersionOptions(id: string) {
  return getJson<ComponentVersionOptions>(`/api/components/${encodeURIComponent(id)}/versions`);
}

export async function openPath(path: string, select = false) {
  return postJson<{ path: string; openedPath: string; mode: string }>("/api/system/open-path", { path, select }, uiText.apiError.openPathFailed);
}

export async function openProperties(path: string) {
  return postJson<{ path: string; openedPath: string; mode: string }>("/api/system/open-properties", { path }, uiText.apiError.openPropertiesFailed);
}

export async function terminateProcesses(targets: SystemProcessIdentity[]) {
  return withProcessActionElevation("terminate", targets,
    () => postJson<SystemProcessOperationResult>("/api/system/processes/terminate", { targets }, uiText.apiError.terminateFailed),
    uiText.apiError.terminateFailed);
}

export async function createProcessDumps(targets: SystemProcessIdentity[]) {
  return withProcessActionElevation("dump", targets,
    () => postJson<SystemProcessOperationResult>("/api/system/processes/dump", { targets }, uiText.apiError.dumpFailed),
    uiText.apiError.dumpFailed);
}

export async function getResourceBreakdown(
  bars: ResourceBarSettings[] | null,
  processDetailSoftwareIds: string[] = [])
{
  if (bars === null && processDetailSoftwareIds.length === 0) {
    const snapshot = await getJson<ResourceBreakdownWireSnapshot>("/api/resource-breakdown/snapshot");
    return decodeResourceBreakdownWireSnapshot(snapshot);
  }

  const params = new URLSearchParams();
  bars?.forEach((bar) => {
    params.append("ids", bar.metricId);
    params.append("modes", `${bar.metricId}:${bar.scaleMode}`);
  });
  processDetailSoftwareIds.forEach((id) => params.append("processDetails", id));
  const snapshot = await getJson<ResourceBreakdownWireSnapshot>(
    `/api/resource-breakdown/snapshot?${params.toString()}`);
  return decodeResourceBreakdownWireSnapshot(snapshot);
}

export async function verifyComponent(id: string) {
  return postJson<{ state: string; message: BackendMessage }>(
    `/api/components/${encodeURIComponent(id)}/verify`, {}, uiText.stores.actionFailed, false,
    { timeoutMs: 120_000 }); // The explicit verification can fetch a component archive.
}

export async function removeManualSoftware(id: string) {
  return requestNoContent(`/api/software/manual/${encodeURIComponent(id)}`, {
    method: "DELETE", fallbackError: uiText.managementFeedback.manualRemoveFailed
  });
}

export async function addManualSoftware(request: ManualSoftwareRequest) {
  return postJson<unknown>("/api/software/manual", request, uiText.apiError.addSoftwareFailed);
}

export async function confirmPortableSoftwareRoot(softwareId: string, rootPath: string) {
  return postJson<PortableSoftwareRootConfirmationResult>(
    "/api/software/portable/root",
    { softwareId, rootPath },
    uiText.apiError.confirmRootFailed);
}

export async function getGpuPlacementSoftwareSettings(softwareId: string, softwareName: string, softwareKind?: string, signal?: AbortSignal) {
  const params = new URLSearchParams();
  params.set("softwareName", softwareName);
  if (softwareKind) {
    params.set("softwareKind", softwareKind);
  }
  const snapshot = await getJson<GpuPlacementSoftwareSettingsSnapshot>(
    `/api/gpu-placement/software/${encodeURIComponent(softwareId)}?${params.toString()}`, { signal });
  return { ...snapshot, softwarePolicy: decodeSoftwarePolicy(snapshot.softwarePolicy, "$.softwarePolicy") };
}

export async function getDefaultGpuPlacementSoftwarePolicy(softwareId: string, softwareName: string, softwareKind?: string) {
  const params = new URLSearchParams();
  params.set("softwareName", softwareName);
  if (softwareKind) {
    params.set("softwareKind", softwareKind);
  }

  return decodeSoftwarePolicy(await getJson<unknown>(
    `/api/gpu-placement/software/${encodeURIComponent(softwareId)}/policy/default?${params.toString()}`));
}

export async function saveGpuPlacementSoftwarePolicy(policy: GpuPlacementSoftwarePolicy) {
  const result = await saveRuntimePolicy<GpuPlacementSoftwarePolicy>(
    `/api/gpu-placement/software/${encodeURIComponent(policy.softwareId)}/policy`,
    policy,
    uiText.apiError.saveGpuSoftwarePolicyFailed);
  return { ...result, policy: decodeSoftwarePolicy(result.policy, "$.policy") };
}

export async function saveGpuPlacementProcessPolicy(policy: GpuPlacementProcessPolicy) {
  return saveRuntimePolicy<GpuPlacementProcessPolicy>(
    "/api/gpu-placement/process-policy", policy, uiText.apiError.saveGpuProcessPolicyFailed);
}

function saveRuntimePolicy<T>(url: string, body: unknown, fallbackError: string) {
  return writeRuntimePolicy<T>(url, "PUT", body, fallbackError);
}

function resetRuntimePolicy<T>(url: string, fallbackError: string) {
  return writeRuntimePolicy<T>(url, "DELETE", undefined, fallbackError);
}

async function writeRuntimePolicy<T>(url: string, method: string, body: unknown, fallbackError: string): Promise<RuntimePolicySaveResult<T>> {
  try {
    return decodeRuntimePolicySaveResult<T>(await requestJson<unknown>(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
      fallbackError
    }));
  } catch (error) {
    const payload = error instanceof ApiRequestError
      ? error.payload as RuntimePolicySaveResult<T> | null : null;
    if (payload?.policy && (payload.runtimeApplicationDisposition === "savedNotApplied"
      || payload.startupInterceptionDisposition === "applyFailed")) {
      return decodeRuntimePolicySaveResult<T>(payload);
    }
    throw error;
  }
}

export async function observeGpuPlacementProcesses(request: GpuPlacementProcessObservationRequest, signal?: AbortSignal) {
  return postJson<GpuPlacementSoftwareProcessHistory>(
    "/api/gpu-placement/process-history/observe",
    request,
    uiText.apiError.recordGpuProcessHistoryFailed, false, { signal });
}

export async function getHostManagerSmartCoordinatorStatus() {
  return decodeHostManagerSmartCoordinatorStatus(
    await getJson<unknown>("/api/optimization/smart/status"));
}

export async function getHostManagerRollbackState() {
  return decodeHostManagerRollbackState(
    await getJson<unknown>("/api/optimization/smart/state"));
}

export async function setHostManagerSmartCoordinatorMode(mode: AppOptimizationMode) {
  return decodeHostManagerSmartCoordinatorStatus(await postJson<unknown>(
    "/api/optimization/smart/mode",
    { mode },
    uiText.apiError.switchSmartModeFailed));
}

export async function dismissOptimizationReport(id: string) {
  return postJson<TrustedOptimizationTarget>(
    `/api/optimization/reports/${encodeURIComponent(id)}/dismiss`,
    {},
    uiText.apiError.dismissReportFailed);
}

export async function removeOptimizationTrust(id: string) {
  await requestNoContent(`/api/optimization/trust/${encodeURIComponent(id)}`, {
    method: "DELETE",
    fallbackError: uiText.apiError.removeTrustFailed
  });
}
