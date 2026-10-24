import { uiText } from "../text.ts";
import { renderBackendMessage } from "./backendMessage.ts";
import type { BackendMessage } from "../types";
import type { UserDetailSection } from "./userDetails";
import {
  compactUserDetailSections,
  userDetailItem,
  userDetailSection
} from "./userDetails.ts";

const internalImplementationPattern = /\b(?:provider|shim|hook|loopback|jsonl|webview2|dxgkrnl|vidmm|fileio|diskio|ip helper|affinity|native ui|api|gc)\b|目标接收池|重算账本|运行中重建诱导|内部控制接口|热路径|闸门|软件级信封|调用栈|堆栈|exception|system\.[a-z]|at [a-z0-9_.]+\(/i;

// 后端状态值 -> 用户状态键；文字本身由当前语言的文案包提供。
type UserStateKey = keyof typeof uiText.status.state;

const commonStateKeys: Record<string, UserStateKey> = {
  active: "available",
  installed: "installed",
  installed_unverified: "installedUnverified",
  ready_to_install: "readyToInstall",
  manual_download_required: "manualDownload",
  provider_missing: "unavailable",
  driver_missing: "missing",
  runtime_available: "available",
  runtime_active: "available",
  readyToInstall: "readyToInstall",
  manualDownloadRequired: "manualDownload",
  runtimeAvailable: "available",
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
  if (isApiRequestError(error) && error.kind === "http"
    && "status" in error && error.status === 403 && "payload" in error
    && error.payload && typeof error.payload === "object"
    && "code" in error.payload && error.payload.code === "ProcessActionRequiresElevation") {
    return uiText.softwareActions.processActionNeedsElevation;
  }
  if (isApiRequestError(error)
    && (error.kind === "timeout" || error.kind === "invalid-response" || error.kind === "aborted")) {
    return sentence(safeFallback(error.userMessage, fallback));
  }
  return sentence(safeFallback(fallback, uiText.status.actionFailed));
}

export function userFacingMessage(value: unknown, fallback: string) {
  const text = String(value ?? "").trim();
  if (text === "已向用户桌面请求启动 Windows 注册表提供的卸载器；需要提升时由 Windows 提示。完成后请刷新软件清单。")
    return uiText.softwareActions.uninstallerLaunchRequested;
  if (text === "The requesting user session is no longer available." || text === "The interactive session user changed.")
    return uiText.softwareActions.uninstallerSessionUnavailable;
  if (!text || internalImplementationPattern.test(text) || looksLikeRawCode(text)) {
    // The caller supplies localized product copy here. Short words such as
    // "Preparing" must not be classified as backend identifiers.
    return sentence(fallback.trim() || uiText.status.stateUpdated);
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

  const key = commonStateKeys[text];
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

export function userFacingDateTime(value: unknown, fallback = uiText.status.unknownTime) {
  const text = String(value ?? "").trim();
  if (!text) {
    return fallback;
  }

  const date = new Date(text);
  return Number.isNaN(date.getTime()) ? fallback : date.toLocaleString();
}

/**
 * 分组键 → 界面上的字样。
 *
 * 键是后端定的（见 `MetricGroups`），一个键对应一个**设备**。
 * 显卡是每块一组，键形如 `gpu.0`，字样就是 `GPU0`。
 *
 * **认不出的键原样显示，不再统一叫「系统」。** 先前那个兜底把
 * `GPU0` / `GPU1` 一起吞进了「系统」—— 分组错了，界面上却看不出错，
 * 只看得出显卡莫名其妙跑到系统底下。认不出就把键摆出来，
 * 一眼能看到是哪个键没接上。
 */
export function userFacingMetricGroup(value: unknown) {
  const group = uiText.status.metricGroup;
  const key = String(value ?? "").trim().toLocaleLowerCase();
  const gpuIndex = /^gpu\.(\d+)$/.exec(key);
  if (gpuIndex) {
    return `GPU${gpuIndex[1]}`;
  }

  switch (key) {
    case "target-software": return uiText.performanceOverlay.group;
    case "cpu": return group.cpu;
    case "gpu": return group.gpu;
    case "memory": return group.memory;
    case "virtual-memory": return group.virtualMemory;
    case "disk": return group.disk;
    case "network": return group.network;
    case "motherboard": return group.motherboard;
    case "fan": return group.fan;
    case "hardwaremonitor": return group.hardwareMonitor;
    case "system": return group.system;
    // 认不出的分组（包括设备字段的设备名）原样显示，不改大小写。
    default: return String(value ?? "").trim() || group.system;
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
    case "fan-control-core": return names.fanControlCore;
    case "hardware-bridge": return names.hardwareBridge;
    case "amd-smu-pawnio-provider": return names.amdSmuPawnIo;
    case "hardware-sensors-pawnio-provider": return names.hardwareSensorsPawnIo;
    case "intel-msr-pawnio-provider": return names.intelMsrPawnIo;
    case "librehardwaremonitor-provider": return names.libreHardwareMonitor;
    case "shared-webview2-runtime": return names.sharedWebView2Runtime;
    case "notebook-oem-fan-provider": return names.notebookOemFan;
    case "nvidia-nvml-provider": return names.nvidiaNvml;
    case "nvidia-nvapi-provider": return names.nvidiaNvapi;
    case "amd-adlx-provider": return names.amdAdlx;
    default: return safeUserFact(fallback) || uiText.status.component.nameFallback;
  }
}

export function componentPurpose(componentId: unknown) {
  const purposes = uiText.status.component.purposes;
  switch (String(componentId ?? "").trim().toLocaleLowerCase()) {
    case "fan-control-core": return purposes.fanControlCore;
    case "hardware-bridge": return purposes.hardwareBridge;
    case "amd-smu-pawnio-provider": return purposes.amdSmu;
    case "hardware-sensors-pawnio-provider": return purposes.hardwareSensorsPawnIo;
    case "intel-msr-pawnio-provider": return purposes.intelMsrPawnIo;
    case "librehardwaremonitor-provider": return purposes.motherboardMemorySensors;
    case "nvidia-nvml-provider": return purposes.nvidiaNvml;
    case "nvidia-nvapi-provider": return purposes.nvidiaNvapi;
    case "amd-adlx-provider": return purposes.amdGpu;
    case "notebook-oem-fan-provider": return purposes.notebookOemFan;
    case "shared-webview2-runtime": return purposes.sharedWebView2Runtime;
    default:
      return uiText.status.component.purposeFallback;
  }
}

// 组件目录里只有硬件监控一类。
export function componentCategory() {
  return uiText.status.component.categories.hardwareMonitoring;
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
