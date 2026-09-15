import type {
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
  GpuPlacementProcessPolicySaveResult,
  GpuPlacementSoftwarePolicy,
  GpuPlacementSoftwareProcessHistory,
  GpuPlacementSoftwareSettingsSnapshot,
  GpuPerformanceScoreOverrideRequest,
  GpuPerformanceScoreOverrideResult,
  GpuPerformanceScoreSnapshot,
  GpuSpecializedTelemetrySnapshot,
  ManagedComponent,
  LocalOnlineSearchResult,
  ManualSoftwareRequest,
  MetricDefinition,
  OptimizationReportOverview,
  PortableSoftwareRootConfirmationResult,
  ResourceBarSettings,
  ResourceTableColumnSettings,
  ResourceTableSnapshot,
  ResourceTableViewMode,
  AppOptimizationMode,
  HostManagerSmartCoordinatorStatus,
  SoftwareRecord,
  SystemProcessIdentity,
  SystemProcessOperationResult,
  TrustedOptimizationTarget
} from "./types";
import {
  decodeResourceBreakdownWireSnapshot,
  decodeResourceMonitorWireSnapshot,
  decodeResourceTableWireSnapshot,
  type ResourceBreakdownWireSnapshot,
  type ResourceMonitorWireSnapshot
} from "./api/resourceBreakdownWire";
import { decodeHostManagerRollbackState } from "./api/hostManagerRollbackWire";
import { ApiRequestError, requestJson, requestNoContent } from "./api/httpTransport";

export async function getJson<T>(
  url: string,
  options: { signal?: AbortSignal; timeoutMs?: number } = {}
): Promise<T> {
  return requestJson<T>(url, {
    cache: "no-store",
    fallbackError: "读取数据失败，请稍后重试",
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

export async function getMetricCatalog() {
  return getJson<MetricDefinition[]>("/api/metrics/catalog");
}

export async function getDashboardSettings() {
  return getJson<DashboardSettingsResult>("/api/settings/dashboard");
}

export async function saveDashboardSettings(settings: DashboardSettings) {
  return requestJson<DashboardSettingsResult>("/api/settings/dashboard", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
    fallbackError: "保存设置失败"
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
    "生成本地 AI 密钥失败");
}

export async function revokeAiGatewayCredential(credentialId: string) {
  return deleteJson<{ revoked: boolean }>(
    `/api/ai-gateway/credentials/${encodeURIComponent(credentialId)}`,
    "撤销本地 AI 密钥失败");
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
  return putJson<CpuCorePerformanceOverrideResult>(
    "/api/cpu/topology/performance-overrides",
    request,
    "保存 CPU 核心性能分失败");
}

export async function resetCpuCorePerformanceOverrides(cpuName: string) {
  const params = new URLSearchParams();
  params.set("cpuName", cpuName);
  return deleteJson<CpuCorePerformanceOverrideResult>(
    `/api/cpu/topology/performance-overrides?${params.toString()}`,
    "重置 CPU 核心性能分失败");
}

export async function getGpuPerformanceScoreOverrides() {
  return getJson<GpuPerformanceScoreOverrideResult>("/api/gpu/performance-overrides");
}

export async function getGpuPerformanceScores() {
  return getJson<GpuPerformanceScoreSnapshot>("/api/gpu/performance-scores");
}

export async function saveGpuPerformanceScoreOverrides(request: GpuPerformanceScoreOverrideRequest) {
  return putJson<GpuPerformanceScoreOverrideResult>(
    "/api/gpu/performance-overrides",
    request,
    "保存 GPU 性能分失败");
}

export async function resetGpuPerformanceScoreOverrides() {
  return deleteJson<GpuPerformanceScoreOverrideResult>(
    "/api/gpu/performance-overrides",
    "重置 GPU 性能分失败");
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
  return postJson<LocalOnlineSearchResult>("/api/system/search-online", { query }, "在线搜索失败");
}

/** 版本对话框的数据源：已验证版本 + 最新版本。最新版本解析失败时该项带原因返回。 */
export async function fetchComponentVersionOptions(id: string) {
  return getJson<ComponentVersionOptions>(`/api/components/${encodeURIComponent(id)}/versions`);
}

export async function openPath(path: string, select = false) {
  return postJson<{ message?: string; openedPath?: string }>("/api/system/open-path", { path, select }, "打开路径失败");
}

export async function openProperties(path: string) {
  return postJson<{ message?: string; openedPath?: string }>("/api/system/open-properties", { path }, "打开属性失败");
}

export async function terminateProcesses(targets: SystemProcessIdentity[]) {
  return postJson<SystemProcessOperationResult>("/api/system/processes/terminate", { targets }, "结束任务失败");
}

export async function createProcessDumps(targets: SystemProcessIdentity[]) {
  return postJson<SystemProcessOperationResult>("/api/system/processes/dump", { targets }, "创建内存转储文件失败");
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

export async function getResourceMonitor(
  bars: ResourceBarSettings[] | null,
  sampleMetricIds: string[] | null,
  columns: ResourceTableColumnSettings[] | null,
  sortColumnId: string,
  sortDirection: string,
  expandedSoftwareIds: string[],
  processDetailSoftwareIds: string[],
  tableMode: ResourceTableViewMode)
{
  const params = new URLSearchParams();
  bars?.forEach((bar) => {
    params.append("ids", bar.metricId);
    params.append("modes", `${bar.metricId}:${bar.scaleMode}`);
  });
  sampleMetricIds?.forEach((id) => params.append("sampleIds", id));
  const visibleColumns = columns?.filter((column) => column.visible).map((column) => column.id) ?? [];
  visibleColumns.forEach((columnId) => params.append("columns", columnId));
  params.set("sort", `${sortColumnId}:${sortDirection === "asc" ? "asc" : "desc"}`);
  params.set("mode", tableMode === "process" ? "process" : "software");
  expandedSoftwareIds.forEach((id) => params.append("expanded", id));
  processDetailSoftwareIds.forEach((id) => params.append("processDetails", id));
  const snapshot = await getJson<ResourceMonitorWireSnapshot>(
    `/api/resource-monitor/snapshot?${params.toString()}`);
  return decodeResourceMonitorWireSnapshot(snapshot);
}

export async function getResourceTable(
  columns: ResourceTableColumnSettings[] | null,
  sortColumnId: string,
  sortDirection: string,
  expandedSoftwareIds: string[],
  tableMode: ResourceTableViewMode)
{
  const params = new URLSearchParams();
  const visibleColumns = columns?.filter((column) => column.visible).map((column) => column.id) ?? [];
  visibleColumns.forEach((columnId) => params.append("columns", columnId));
  params.set("sort", `${sortColumnId}:${sortDirection === "asc" ? "asc" : "desc"}`);
  params.set("mode", tableMode === "process" ? "process" : "software");
  expandedSoftwareIds.forEach((id) => params.append("expanded", id));
  const snapshot = await getJson<unknown>(
    `/api/resource-table/snapshot?${params.toString()}`);
  return decodeResourceTableWireSnapshot(snapshot);
}

export async function getComponents() {
  return getJson<ManagedComponent[]>("/api/components");
}

export async function getSoftware(refresh = false) {
  return getJson<SoftwareRecord[]>(refresh ? "/api/software?refresh=true" : "/api/software");
}

export async function addManualSoftware(request: ManualSoftwareRequest) {
  return postJson<unknown>("/api/software/manual", request, "添加软件失败");
}

export async function confirmPortableSoftwareRoot(softwareId: string, rootPath: string) {
  return postJson<PortableSoftwareRootConfirmationResult>(
    "/api/software/portable/root",
    { softwareId, rootPath },
    "确认软件根目录失败");
}

export async function getGpuPlacementSoftwareSettings(softwareId: string, softwareName: string, softwareKind?: string) {
  const params = new URLSearchParams();
  params.set("softwareName", softwareName);
  if (softwareKind) {
    params.set("softwareKind", softwareKind);
  }
  return getJson<GpuPlacementSoftwareSettingsSnapshot>(
    `/api/gpu-placement/software/${encodeURIComponent(softwareId)}?${params.toString()}`);
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
  return putJson<GpuPlacementSoftwarePolicy>(
    `/api/gpu-placement/software/${encodeURIComponent(policy.softwareId)}/policy`,
    policy,
    "保存 GPU 调度软件策略失败");
}

export async function saveGpuPlacementProcessPolicy(policy: GpuPlacementProcessPolicy) {
  try {
    return await requestJson<GpuPlacementProcessPolicySaveResult>("/api/gpu-placement/process-policy", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(policy),
      fallbackError: "保存 GPU 调度进程策略失败"
    });
  } catch (error) {
    const payload = error instanceof ApiRequestError
      ? error.payload as GpuPlacementProcessPolicySaveResult | null
      : null;
    if (payload?.policy && (
      payload.runtimeApplicationDisposition === "savedNotApplied"
      || payload.startupInterceptionDisposition === "applyFailed"
    )) {
      return payload;
    }
    throw error;
  }
}

export async function observeGpuPlacementProcesses(request: GpuPlacementProcessObservationRequest) {
  return postJson<GpuPlacementSoftwareProcessHistory>(
    "/api/gpu-placement/process-history/observe",
    request,
    "记录 GPU 调度进程历史失败");
}

export async function getHostManagerSmartCoordinatorStatus() {
  return getJson<HostManagerSmartCoordinatorStatus>("/api/optimization/smart/status");
}

export async function getHostManagerRollbackState() {
  return decodeHostManagerRollbackState(
    await getJson<unknown>("/api/optimization/smart/state"));
}

export async function setHostManagerSmartCoordinatorMode(mode: AppOptimizationMode) {
  return postJson<HostManagerSmartCoordinatorStatus>(
    "/api/optimization/smart/mode",
    { mode },
    "切换智能优化模式失败");
}

export async function dismissOptimizationReport(id: string) {
  return postJson<TrustedOptimizationTarget>(
    `/api/optimization/reports/${encodeURIComponent(id)}/dismiss`,
    {},
    "忽略报告失败");
}

export async function removeOptimizationTrust(id: string) {
  await requestNoContent(`/api/optimization/trust/${encodeURIComponent(id)}`, {
    method: "DELETE",
    fallbackError: "取消信任失败"
  });
}
