import { readDashboardPreferences, saveDashboardPreferences } from "./preferences/dashboardPreferences.ts";
import {
  expectedOverlayInjectionModules,
  overlayInjectionModules,
  overlayStartupModuleIds,
  startupModuleSelectionDecoder,
  type StartupModuleSelection
} from "./features/overlay/startupModuleSelection.ts";
import { getAppSettings, saveAppSettingsPatch } from "./data/appSettings/appSettingsApi.ts";
import { decodePerformanceOverlayAppSettings, findPerformanceOverlayEntry, performanceOverlaySoftwareDecoder } from "./data/appSettings/performanceOverlaySettingsDecoder.ts";
import type { RequestClient } from "./frontendRuntime/request/RequestClient.ts";
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
  OverlayInjectionModules,
  PerformanceOverlayEntry,
  PerformanceOverlayEditorSettings,
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
import { overlayItemsIssue } from "./features/overlay/overlayItems.ts";
import { cpuTopologyDecoder, cpuResidencyDecoder, cpuExclusiveBindingsDecoder } from "./data/cpu/cpuSourceDecoders.ts";
import { decodeRuntimePolicySaveResult } from "./api/runtimePolicySaveResultDecoder.ts";
import { decodeSoftwarePolicy } from "./data/softwarePolicy/softwarePolicyDecoder.ts";
import { decodeBaseScoreResolution, decodeProcessBaseScores } from "./data/softwarePolicy/baseScoreResolutionDecoder.ts";
import { uiText } from "./text.ts";
import { exactProcessActionResult } from "./processes/processActionResult.ts";

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

type OverlayRequestClient = Pick<RequestClient, "request">;

function performanceOverlaySoftwarePath(softwareId: string) {
  return `/api/performance-overlay/software/${encodeURIComponent(softwareId)}`;
}

function overlaySettings(softwareId: string, revision: string, stored: boolean,
    injectionModules: OverlayInjectionModules, entry: PerformanceOverlayEntry): PerformanceOverlaySettings {
  return { softwareId, revision, stored, injectionModules, ...entry, items: entry.items.map((item) => ({ ...item })) };
}

/** 浮窗设置里属于后端条目的字段（不含软件 ID、修订号、是否存着、启动模块登记）。 */
export function performanceOverlayEntry(settings: PerformanceOverlaySettings): PerformanceOverlayEntry {
  const { enabled, mode, items, sizeMode, fontSizePx, regionWidthRatio, regionHeightRatio, anchor, marginPx } = settings;
  return { enabled, mode, items: items.map((item) => ({ ...item })), sizeMode, fontSizePx,
    regionWidthRatio, regionHeightRatio, anchor, marginPx };
}

/**
 * 一个软件的浮窗设置：整份读后端（GET /api/performance-overlay/software/{id}，没有条目时是后端的默认条目，关），
 * 是否已登记注入看后端的启动模块选择，一并读出。signal 取消两次读取。
 */
export async function getPerformanceOverlaySettings(requestClient: OverlayRequestClient, softwareId: string,
    signal?: AbortSignal): Promise<PerformanceOverlayEditorSettings> {
  const [read, selection] = await Promise.all([
    requestClient.request({
      key: "performance-overlay.software.get",
      url: performanceOverlaySoftwarePath(softwareId),
      fallbackError: uiText.apiError.readFailed,
      decoder: performanceOverlaySoftwareDecoder,
      signal,
      request: { method: "GET" }
    }),
    getStartupModuleSelection(softwareId, signal)
  ]);
  return { ...overlaySettings(read.softwareId, read.revision, read.stored, overlayInjectionModules(selection), read.settings),
    editingConstraints: read.editingConstraints };
}

/**
 * 设置 → 性能浮层的总览：后端设置 performanceOverlay 里启用的每个软件，带它的启动模块登记（和浮窗分页同一读法）。
 * 不读运行状态。signal 取消设置和各软件启动模块的读取。
 */
export async function listEnabledPerformanceOverlays(requestClient: OverlayRequestClient,
    signal: AbortSignal): Promise<PerformanceOverlaySettings[]> {
  const result = await getAppSettings(requestClient, signal);
  const section = decodePerformanceOverlayAppSettings(result.settings.performanceOverlay, "$.settings.performanceOverlay");
  const enabled = Object.entries(section.software).filter(([, entry]) => entry.enabled);
  return Promise.all(enabled.map(async ([softwareId, entry]) => overlaySettings(softwareId, result.revision, true,
    overlayInjectionModules(await getStartupModuleSelection(softwareId, signal)), entry)));
}

function startupModuleSelectionPath(softwareId: string) {
  return `/api/startup-modules/software/${encodeURIComponent(softwareId)}`;
}

async function getStartupModuleSelection(softwareId: string, signal?: AbortSignal): Promise<StartupModuleSelection> {
  return startupModuleSelectionDecoder.decode(await getJson<unknown>(startupModuleSelectionPath(softwareId), { signal }));
}

/** 启动模块已经在后端改好，但浮窗设置没有保存成功。界面据此提示“启动模块已更新、浮窗设置未保存”。 */
export class OverlaySettingsSaveError extends Error {
  constructor(cause: unknown) {
    super("The startup modules were saved, but the overlay settings were not.", { cause });
    this.name = "OverlaySettingsSaveError";
  }
}

/** 把一个软件的条目写进后端设置（整条替换），返回后端归一化后的条目和新修订号。 */
async function patchPerformanceOverlayEntry(requestClient: OverlayRequestClient, softwareId: string, expectedRevision: string,
    entry: Partial<PerformanceOverlayEntry>) {
  const result = await saveAppSettingsPatch(requestClient, expectedRevision,
    { performanceOverlay: { software: { [softwareId]: entry } } });
  const saved = findPerformanceOverlayEntry(result.settings.performanceOverlay, softwareId);
  if (!saved) throw new Error(`performance-overlay-entry-missing: ${softwareId}`);
  return { softwareId: saved.softwareId, revision: result.revision, entry: saved.entry };
}

/**
 * 保存浮窗分页的草稿：开关或方式改变，或后端登记的注入模块与这里的选择不一致时，先按选择更新启动模块
 * （启用且注入 => 两个浮层模块都登记），再把整条写进后端设置（expectedRevision 取读到时的修订号）。
 */
export async function savePerformanceOverlaySettings(requestClient: OverlayRequestClient,
    settings: PerformanceOverlayEditorSettings, saved: PerformanceOverlayEditorSettings): Promise<PerformanceOverlayEditorSettings> {
  const entry = performanceOverlayEntry(settings);
  // 不合规的项不写进后端设置（界面在保存前就不让保存，这里拦住其他调用方）。
  const itemsIssue = overlayItemsIssue(entry.items, saved.editingConstraints);
  if (itemsIssue !== null) throw new Error(`overlay-items-invalid: ${itemsIssue}`);
  const expected = expectedOverlayInjectionModules(entry);
  let injectionModules = saved.injectionModules;
  let modulesChanged = false;
  if (saved.enabled !== entry.enabled || saved.mode !== entry.mode || saved.injectionModules !== expected) {
    const path = startupModuleSelectionPath(settings.softwareId);
    const current = await getStartupModuleSelection(settings.softwareId);
    const overlayIds: readonly string[] = overlayStartupModuleIds;
    const unrelated = current.moduleIds.filter(id => !overlayIds.includes(id));
    const moduleIds = [...unrelated, ...(expected === "complete" ? overlayStartupModuleIds : [])];
    if (JSON.stringify([...current.moduleIds].sort()) !== JSON.stringify([...moduleIds].sort())) {
      await putJson<StartupModuleSelection>(path, { softwareId: settings.softwareId, moduleIds }, uiText.apiError.saveGpuSoftwarePolicyFailed);
      modulesChanged = true;
    }
    injectionModules = expected;
  }
  try {
    const result = await patchPerformanceOverlayEntry(requestClient, settings.softwareId, saved.revision, entry);
    return { ...overlaySettings(result.softwareId, result.revision, true, injectionModules, result.entry),
      editingConstraints: saved.editingConstraints };
  } catch (error) {
    throw modulesChanged ? new OverlaySettingsSaveError(error) : error;
  }
}

/** 恢复默认外观：只写开关和方式，其余字段由后端展开成默认值（项、大小、位置）。 */
export async function resetPerformanceOverlayPresentation(requestClient: OverlayRequestClient,
    saved: PerformanceOverlayEditorSettings): Promise<PerformanceOverlayEditorSettings> {
  const result = await patchPerformanceOverlayEntry(requestClient, saved.softwareId, saved.revision,
    { enabled: saved.enabled, mode: saved.mode });
  return { ...overlaySettings(result.softwareId, result.revision, true, saved.injectionModules, result.entry),
    editingConstraints: saved.editingConstraints };
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
  return exactProcessActionResult("terminate", targets,
    await postJson<unknown>("/api/system/processes/terminate", { targets }, uiText.apiError.terminateFailed),
    uiText.apiError.terminateFailed);
}

export async function createProcessDumps(targets: SystemProcessIdentity[]) {
  return exactProcessActionResult("dump", targets,
    await postJson<unknown>("/api/system/processes/dump", { targets }, uiText.apiError.dumpFailed),
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
  return {
    ...snapshot,
    softwarePolicy: decodeSoftwarePolicy(snapshot.softwarePolicy, "$.softwarePolicy"),
    baseScore: decodeBaseScoreResolution(snapshot.baseScore, "$.baseScore"),
    processBaseScores: decodeProcessBaseScores(snapshot.processBaseScores, "$.processBaseScores")
  };
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
