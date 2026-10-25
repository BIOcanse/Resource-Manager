import { decodeSoftwareRecords } from "./features/management/softwareRecordDecoder.ts";
import { decodeManagedComponents } from "./features/management/componentDecoder.ts";
import type {
  BackendMessage,
  AiGatewayCompatibilityProfile,
  AiGatewayCredentialCreatedView,
  AiGatewayCredentialView,
  ComponentVersionOptions,
  CpuCorePerformanceOverrideRequest,
  CpuCorePerformanceOverrideResult,
  CpuExclusiveBindingSnapshot,
  CpuCoreResidencySnapshot,
  CpuTopologySnapshot,
  DashboardSettings,
  DashboardSettingsResult,
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
  MetricDefinition,
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
} from "./api/resourceBreakdownWire";
import { decodeHostManagerRollbackState } from "./api/hostManagerRollbackWire";
import { decodeHostManagerSmartCoordinatorStatus } from
  "./data/optimization/hostManagerSmartStatusSource.ts";
import { ApiRequestError, requestJson, requestNoContent } from "./api/httpTransport";
import { reportRuleSettingsDecoder } from "./data/appSettings/reportRuleSettingsDecoder.ts";
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
  return getJson<MetricDefinition[]>("/api/metrics/catalog");
}

export async function getPerformanceOverlaySettings(softwareId: string) {
  return getJson<PerformanceOverlaySettings>(
    `/api/performance-overlay/software/${encodeURIComponent(softwareId)}`);
}

export async function getPerformanceOverlayMetricCatalog() {
  return getJson<MetricDefinition[]>("/api/performance-overlay/metrics/catalog");
}

export async function savePerformanceOverlaySettings(settings: PerformanceOverlaySettings) {
  return putJson<PerformanceOverlaySettings>(
    `/api/performance-overlay/software/${encodeURIComponent(settings.softwareId)}`,
    settings,
    uiText.apiError.saveGpuSoftwarePolicyFailed);
}

export async function getDashboardSettings() {
  return getJson<DashboardSettingsResult>("/api/settings/dashboard");
}

export async function saveDashboardSettings(settings: DashboardSettings) {
  return requestJson<DashboardSettingsResult>("/api/settings/dashboard", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
    fallbackError: uiText.apiError.saveSettingsFailed
  });
}

export async function getAiGatewayCredentials() {
  return getJson<AiGatewayCredentialView[]>("/api/ai-gateway/credentials");
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
  return getJson<CpuTopologySnapshot | null>("/api/cpu/topology");
}

export async function getCpuResidency() {
  return getJson<CpuCoreResidencySnapshot>("/api/cpu/residency");
}

export async function getCpuExclusiveBindings() {
  return getJson<CpuExclusiveBindingSnapshot>("/api/cpu/topology/exclusive-bindings");
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

export async function getComponents() {
  return decodeManagedComponents(await getJson<unknown>("/api/components"));
}

export async function verifyComponent(id: string) {
  return postJson<{ state: string; message: BackendMessage }>(
    `/api/components/${encodeURIComponent(id)}/verify`, {}, uiText.stores.actionFailed, false,
    { timeoutMs: 120_000 }); // The explicit verification can fetch a component archive.
}

export async function getSoftware(refresh = false) {
  return decodeSoftwareRecords(await getJson<unknown>(refresh ? "/api/software?refresh=true" : "/api/software"));
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
  return getJson<GpuPlacementSoftwareSettingsSnapshot>(
    `/api/gpu-placement/software/${encodeURIComponent(softwareId)}?${params.toString()}`, { signal });
}

export async function getDefaultGpuPlacementSoftwarePolicy(softwareId: string, softwareName: string, softwareKind?: string) {
  const params = new URLSearchParams();
  params.set("softwareName", softwareName);
  if (softwareKind) {
    params.set("softwareKind", softwareKind);
  }

  return getJson<GpuPlacementSoftwarePolicy>(
    `/api/gpu-placement/software/${encodeURIComponent(softwareId)}/policy/default?${params.toString()}`);
}

export async function saveGpuPlacementSoftwarePolicy(policy: GpuPlacementSoftwarePolicy) {
  return saveRuntimePolicy<GpuPlacementSoftwarePolicy>(
    `/api/gpu-placement/software/${encodeURIComponent(policy.softwareId)}/policy`,
    policy,
    uiText.apiError.saveGpuSoftwarePolicyFailed);
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
    return await requestJson<RuntimePolicySaveResult<T>>(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
      fallbackError
    });
  } catch (error) {
    const payload = error instanceof ApiRequestError
      ? error.payload as RuntimePolicySaveResult<T> | null : null;
    if (payload?.policy && (payload.runtimeApplicationDisposition === "savedNotApplied"
      || payload.startupInterceptionDisposition === "applyFailed")) {
      return payload;
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
