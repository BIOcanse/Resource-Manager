import { uiText } from "../text.ts";
import { renderBackendMessage } from "./backendMessage.ts";
import type { BackendMessage, SoftwareDataMigrationRecord } from "../types";
import type { UserDetailSection } from "./userDetails";
import {
  compactUserDetailSections,
  userDetailItem,
  userDetailSection
} from "./userDetails";

const internalImplementationPattern = /\b(?:provider|shim|hook|loopback|jsonl|webview2|dxgkrnl|vidmm|fileio|ip helper|affinity|native ui|api|gc)\b|目标接收池|重算账本|运行中重建诱导|内部控制接口|热路径|闸门|软件级信封|调用栈|堆栈|exception|system\.[a-z]|at [a-z0-9_.]+\(/i;

// 后端状态值 -> 用户状态键；文字本身由当前语言的文案包提供。
type UserStateKey = keyof typeof uiText.status.state;

const commonStateKeys: Record<string, UserStateKey> = {
  active: "available",
  installed: "installed",
  installedunverified: "installedUnverified",
  readytoinstall: "readyToInstall",
  runtimeavailable: "available",
  running: "running",
  stopped: "stopped",
  starting: "starting",
  ready: "ready",
  refreshing: "refreshing",
  warming: "warming",
  failed: "failed",
  unavailable: "unavailable",
  disabled: "disabled",
  enabled: "enabled",
  queued: "queued",
  canceling: "canceling",
  canceled: "canceled",
  succeeded: "completed",
  completed: "completed",
  restored: "restored",
  missing: "missing",
  unknown: "unknown",
  "已激活": "available",
  "已安装": "installed",
  "已安装待验证": "installedUnverified",
  "可安装": "readyToInstall",
  "可下载": "downloadable",
  "需手动下载": "manualDownload",
  "provider 未接入": "unavailable",
  "未安装": "notInstalled",
  "可用": "available",
  "运行中": "running",
  "已停止": "stopped"
};

export function userFacingErrorMessage(error: unknown, fallback: string) {
  if (isApiRequestError(error)
    && (error.kind === "timeout" || error.kind === "invalid-response" || error.kind === "aborted")) {
    return sentence(safeFallback(error.userMessage, fallback));
  }
  return sentence(safeFallback(fallback, uiText.status.actionFailed));
}

export function userFacingMessage(value: unknown, fallback: string) {
  const text = String(value ?? "").trim();
  if (!text || internalImplementationPattern.test(text) || looksLikeRawCode(text)) {
    return sentence(safeFallback(fallback, uiText.status.stateUpdated));
  }

  return sentence(text);
}

export function userFacingLabel(value: unknown, fallback = uiText.status.unknownState) {
  const text = String(value ?? "").trim();
  if (text.toLocaleLowerCase() === "dxgkrnl/vidmm") {
    return uiText.status.systemGraphicsUsage;
  }

  if (!text || internalImplementationPattern.test(text) || looksLikeRawCode(text)) {
    return safeFallback(fallback, uiText.status.unknownState);
  }

  return text;
}

export function userFacingState(value: unknown, fallback = uiText.status.unknownState) {
  const text = String(value ?? "").trim();
  if (!text) {
    return fallback;
  }

  const key = commonStateKeys[text.toLocaleLowerCase()];
  return key ? uiText.status.state[key] : fallback;
}

export function userFacingOptionalValue(value: unknown, fallback = uiText.status.noData) {
  const text = String(value ?? "").trim();
  return !text || text === "--" || text.toLocaleUpperCase() === "N/A" || text === "Unknown"
    ? fallback
    : text;
}

export function userFacingRisk(value: unknown) {
  const risk = uiText.status.risk;
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "low": return risk.low;
    case "medium": return risk.medium;
    case "high": return risk.high;
    case "root": return risk.root;
    case "blocked": return risk.blocked;
    default: return risk.unknown;
  }
}

export function migrationKindLabel(value: unknown) {
  return String(value ?? "").toLocaleLowerCase() === "root"
    ? uiText.status.migration.kindRoot
    : uiText.status.migration.kindData;
}

export function migrationTargetCategoryLabel(value: unknown) {
  return String(value ?? "").toLocaleLowerCase() === "misc"
    ? uiText.status.migration.targetMisc
    : uiText.status.migration.targetUser;
}

export function migrationClassificationLabel(value: unknown) {
  const migration = uiText.status.migration;
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "applicationroot": return migration.classificationApplicationRoot;
    case "userappdata": return migration.classificationUserAppData;
    case "programdata": return migration.classificationProgramData;
    case "directory": return migration.classificationDirectory;
    case "file": return migration.classificationFile;
    case "missing": return migration.classificationMissing;
    default: return migration.classificationUnknown;
  }
}

export function migrationStateLabel(value: unknown) {
  const migration = uiText.status.migration;
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "running": return migration.stateRunning;
    case "stopped": return migration.stateStopped;
    case "completed": return migration.stateCompleted;
    case "restored": return migration.stateRestored;
    case "failed": return migration.stateFailed;
    default: return migration.stateUnknown;
  }
}

export function userFacingDateTime(value: unknown, fallback = uiText.status.unknownTime) {
  const text = String(value ?? "").trim();
  if (!text) {
    return fallback;
  }

  const date = new Date(text);
  return Number.isNaN(date.getTime()) ? fallback : date.toLocaleString();
}

export function userFacingMetricGroup(value: unknown) {
  const group = uiText.status.metricGroup;
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "cpu": return group.cpu;
    case "gpu": return group.gpu;
    case "memory": return group.memory;
    case "virtual memory": return group.virtualMemory;
    case "disk": return group.disk;
    case "network": return group.network;
    case "motherboard": return group.motherboard;
    case "fan": return group.fan;
    case "hardwaremonitor": return group.hardwareMonitor;
    default: return group.system;
  }
}

// 为什么不能选这个指标，后端在目录里已经算好了（可能是「当前没有读数」而不是「缺组件」）。
// 只有后端没给理由时才退回这两句通用说明，不要因为指标声明过组件就断言是缺组件。
export function userFacingMetricUnavailableReason(
  requiredComponentName?: string | null,
  disabledReason?: BackendMessage | null
) {
  if (disabledReason) {
    return renderBackendMessage(disabledReason);
  }

  return requiredComponentName
    ? uiText.status.metricUnavailable.needsComponent
    : uiText.status.metricUnavailable.deviceNotProvided;
}

export function componentDisplayName(componentId: unknown, fallback?: unknown) {
  const names = uiText.status.component.names;
  switch (String(componentId ?? "").trim().toLocaleLowerCase()) {
    case "amd-smu-pawnio-provider": return names.amdSmuPawnIo;
    case "amd-ryzen-master-monitoring-sdk": return names.amdRyzenMaster;
    case "msi-afterburner": return names.msiAfterburner;
    case "librehardwaremonitor-provider": return names.libreHardwareMonitor;
    case "shared-webview2-runtime": return names.sharedWebView2Runtime;
    case "notebook-fancontrol-provider": return names.notebookFanControl;
    case "notebook-oem-fan-provider": return names.notebookOemFan;
    case "windows-performance-toolkit": return names.windowsPerformanceToolkit;
    case "latencymon": return names.latencyMon;
    case "nvidia-nvml-provider": return names.nvidiaNvml;
    case "nvidia-nvapi-provider": return names.nvidiaNvapi;
    case "amd-adlx-provider": return names.amdAdlx;
    case "intel-pcm-provider": return names.intelPcm;
    default: return safeUserFact(fallback) || uiText.status.component.nameFallback;
  }
}

export function componentPurpose(componentId: unknown) {
  const purposes = uiText.status.component.purposes;
  switch (String(componentId ?? "").trim().toLocaleLowerCase()) {
    case "amd-smu-pawnio-provider":
    case "amd-ryzen-master-monitoring-sdk":
      return purposes.amdCpuSensors;
    case "msi-afterburner":
      return purposes.gpuOptionalMonitoring;
    case "librehardwaremonitor-provider":
      return purposes.generalHardwareSensors;
    case "notebook-fancontrol-provider":
    case "notebook-oem-fan-provider":
      return purposes.notebookFan;
    case "windows-performance-toolkit":
    case "latencymon":
      return purposes.latencyAnalysis;
    case "nvidia-nvml-provider":
    case "nvidia-nvapi-provider":
      return purposes.nvidiaGpu;
    case "amd-adlx-provider":
      return purposes.amdGpu;
    case "intel-pcm-provider":
      return purposes.intelCpu;
    default:
      return uiText.status.component.purposeFallback;
  }
}

export function componentCategory(componentId: unknown) {
  const categories = uiText.status.component.categories;
  switch (String(componentId ?? "").trim().toLocaleLowerCase()) {
    case "windows-performance-toolkit":
    case "latencymon":
      return categories.performanceAnalysis;
    case "msi-afterburner":
      return categories.helperTool;
    default:
      return categories.hardwareMonitoring;
  }
}

export function migrationRecordDetails(record: SoftwareDataMigrationRecord): UserDetailSection[] {
  const detail = uiText.status.detail;
  return compactUserDetailSections([
    userDetailSection(detail.migrationContent, [
      userDetailItem(detail.software, record.softwareName || detail.unnamedSoftware),
      userDetailItem(detail.content, migrationKindLabel(record.migrationKind)),
      userDetailItem(detail.savedLocation, migrationTargetCategoryLabel(record.targetCategory)),
      userDetailItem(detail.status, migrationStateLabel(record.state)),
      userDetailItem(detail.createdAt, userFacingDateTime(record.createdAt)),
      record.restoredAt ? userDetailItem(detail.restoredAt, userFacingDateTime(record.restoredAt)) : null
    ]),
    userDetailSection(detail.location, [
      userDetailItem(detail.sourcePath, record.sourcePath),
      userDetailItem(detail.destinationPath, record.destinationPath)
    ])
  ]);
}

export function safeUserFact(value: unknown) {
  const text = String(value ?? "").trim();
  if (!text || internalImplementationPattern.test(text) || looksLikeRawCode(text)) {
    return "";
  }
  return text;
}

function looksLikeRawCode(text: string) {
  return /^[A-Za-z][A-Za-z0-9_.:-]{2,}$/.test(text)
    || /(?:[A-Z][a-z]+){2,}/.test(text)
    || /^0x[0-9a-f]+$/i.test(text);
}

function isApiRequestError(value: unknown): value is {
  name: "ApiRequestError" | "RequestProblem";
  kind: "timeout" | "invalid-response" | "aborted" | string;
  userMessage: string;
} {
  if (!value || typeof value !== "object") {
    return false;
  }

  const error = value as Record<string, unknown>;
  return (error.name === "ApiRequestError" || error.name === "RequestProblem")
    && typeof error.kind === "string"
    && typeof error.userMessage === "string";
}

function safeFallback(value: unknown, fallback: string) {
  const text = String(value ?? "").trim();
  return !text || internalImplementationPattern.test(text) || looksLikeRawCode(text)
    ? fallback
    : text;
}

function sentence(value: string) {
  const text = value.trim();
  return text && !/[。！？.!?]$/.test(text) ? `${text}${uiText.status.sentenceEnd}` : text;
}
