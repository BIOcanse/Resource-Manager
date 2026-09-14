import type { SoftwareDataMigrationRecord } from "../types";
import type { UserDetailSection } from "./userDetails.ts";
import {
  compactUserDetailSections,
  userDetailItem,
  userDetailSection
} from "./userDetails.ts";

const internalImplementationPattern = /\b(?:provider|shim|hook|loopback|jsonl|webview2|dxgkrnl|vidmm|fileio|ip helper|affinity|native ui|api|gc)\b|目标接收池|重算账本|运行中重建诱导|内部控制接口|热路径|闸门|软件级信封|调用栈|堆栈|exception|system\.[a-z]|at [a-z0-9_.]+\(/i;

const commonStateLabels: Record<string, string> = {
  active: "可用",
  installed: "已安装",
  installedunverified: "已安装，等待确认",
  readytoinstall: "可安装",
  runtimeavailable: "可用",
  running: "运行中",
  stopped: "已停止",
  starting: "正在启动",
  ready: "已就绪",
  refreshing: "正在刷新",
  warming: "正在准备",
  failed: "操作失败",
  unavailable: "暂时不可用",
  disabled: "已关闭",
  enabled: "已开启",
  queued: "等待中",
  canceling: "正在取消",
  canceled: "已取消",
  succeeded: "已完成",
  completed: "已完成",
  restored: "已恢复",
  missing: "未找到",
  unknown: "状态未知",
  "已激活": "可用",
  "已安装": "已安装",
  "已安装待验证": "已安装，等待确认",
  "可安装": "可安装",
  "可下载": "可下载",
  "需手动下载": "需要手动下载",
  "provider 未接入": "暂时不可用",
  "未安装": "未安装",
  "可用": "可用",
  "运行中": "运行中",
  "已停止": "已停止"
};

export function userFacingErrorMessage(error: unknown, fallback: string) {
  if (isApiRequestError(error)
    && (error.kind === "timeout" || error.kind === "invalid-response" || error.kind === "aborted")) {
    return sentence(safeFallback(error.userMessage, fallback));
  }
  return sentence(safeFallback(fallback, "操作失败，请稍后重试"));
}

export function userFacingMessage(value: unknown, fallback: string) {
  const text = String(value ?? "").trim();
  if (!text || internalImplementationPattern.test(text) || looksLikeRawCode(text)) {
    return sentence(safeFallback(fallback, "状态已更新"));
  }

  return sentence(text);
}

export function userFacingLabel(value: unknown, fallback = "状态未知") {
  const text = String(value ?? "").trim();
  if (text.toLocaleLowerCase() === "dxgkrnl/vidmm") {
    return "系统图形占用";
  }

  if (!text || internalImplementationPattern.test(text) || looksLikeRawCode(text)) {
    return safeFallback(fallback, "状态未知");
  }

  return text;
}

export function userFacingState(value: unknown, fallback = "状态未知") {
  const text = String(value ?? "").trim();
  if (!text) {
    return fallback;
  }

  return commonStateLabels[text.toLocaleLowerCase()] ?? fallback;
}

export function userFacingOptionalValue(value: unknown, fallback = "暂无数据") {
  const text = String(value ?? "").trim();
  return !text || text === "--" || text.toLocaleUpperCase() === "N/A" || text === "Unknown"
    ? fallback
    : text;
}

export function userFacingRisk(value: unknown) {
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "low": return "低风险";
    case "medium": return "中等风险";
    case "high": return "高风险";
    case "root": return "根目录迁移";
    case "blocked": return "禁止迁移";
    default: return "风险待确认";
  }
}

export function migrationKindLabel(value: unknown) {
  return String(value ?? "").toLocaleLowerCase() === "root" ? "软件根目录" : "软件数据";
}

export function migrationTargetCategoryLabel(value: unknown) {
  return String(value ?? "").toLocaleLowerCase() === "misc" ? "其他数据" : "用户数据";
}

export function migrationClassificationLabel(value: unknown) {
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "applicationroot": return "软件根目录";
    case "userappdata": return "用户应用数据";
    case "programdata": return "共享应用数据";
    case "directory": return "普通目录";
    case "file": return "文件";
    case "missing": return "源位置不存在";
    default: return "待确认数据";
  }
}

export function migrationStateLabel(value: unknown) {
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "running": return "监控中";
    case "stopped": return "已停止";
    case "completed": return "已完成";
    case "restored": return "已恢复";
    case "failed": return "未完成";
    default: return "状态未知";
  }
}

export function userFacingDateTime(value: unknown, fallback = "时间未知") {
  const text = String(value ?? "").trim();
  if (!text) {
    return fallback;
  }

  const date = new Date(text);
  return Number.isNaN(date.getTime()) ? fallback : date.toLocaleString();
}

export function userFacingMetricGroup(value: unknown) {
  switch (String(value ?? "").trim().toLocaleLowerCase()) {
    case "cpu": return "处理器";
    case "gpu": return "图形处理器";
    case "memory": return "内存";
    case "virtual memory": return "虚拟内存";
    case "disk": return "磁盘";
    case "network": return "网络";
    case "motherboard": return "主板";
    case "fan": return "风扇";
    case "hardwaremonitor": return "硬件传感器";
    default: return "系统";
  }
}

export function userFacingMetricUnavailableReason(requiredComponentName?: string | null) {
  return requiredComponentName
    ? "需要安装并启用对应的硬件支持组件。"
    : "当前设备没有提供这项数据。";
}

export function componentDisplayName(componentId: unknown, fallback?: unknown) {
  switch (String(componentId ?? "").trim().toLocaleLowerCase()) {
    case "amd-smu-pawnio-provider": return "AMD 处理器传感支持";
    case "amd-ryzen-master-monitoring-sdk": return "AMD Ryzen 监控支持";
    case "msi-afterburner": return "MSI Afterburner";
    case "librehardwaremonitor-provider": return "通用硬件传感支持";
    case "notebook-fancontrol-provider": return "笔记本风扇监控支持";
    case "notebook-oem-fan-provider": return "笔记本厂商风扇支持";
    case "windows-performance-toolkit": return "Windows 性能分析工具";
    case "latencymon": return "LatencyMon";
    case "nvidia-nvml-provider": return "NVIDIA 显卡监控支持";
    case "nvidia-nvapi-provider": return "NVIDIA 显卡扩展监控支持";
    case "amd-adlx-provider": return "AMD 显卡监控支持";
    case "intel-pcm-provider": return "Intel 处理器监控支持";
    default: return safeUserFact(fallback) || "硬件支持组件";
  }
}

export function componentPurpose(componentId: unknown) {
  switch (String(componentId ?? "").trim().toLocaleLowerCase()) {
    case "amd-smu-pawnio-provider":
    case "amd-ryzen-master-monitoring-sdk":
      return "补充 AMD 处理器的功耗、温度、电压和频率信息。";
    case "msi-afterburner":
      return "提供可选的显卡监控信息。";
    case "librehardwaremonitor-provider":
      return "补充风扇、温度、电压和主板等硬件信息。";
    case "notebook-fancontrol-provider":
    case "notebook-oem-fan-provider":
      return "补充笔记本风扇转速和运行状态。";
    case "windows-performance-toolkit":
    case "latencymon":
      return "用于进一步分析系统延迟和性能问题。";
    case "nvidia-nvml-provider":
    case "nvidia-nvapi-provider":
      return "补充 NVIDIA 显卡的频率、温度、功耗和风扇信息。";
    case "amd-adlx-provider":
      return "补充 AMD 显卡的频率、温度、功耗和风扇信息。";
    case "intel-pcm-provider":
      return "补充 Intel 处理器的功耗、频率和温度信息。";
    default:
      return "为资源管理器补充硬件信息和相关功能。";
  }
}

export function componentCategory(componentId: unknown) {
  switch (String(componentId ?? "").trim().toLocaleLowerCase()) {
    case "windows-performance-toolkit":
    case "latencymon":
      return "性能分析工具";
    case "msi-afterburner":
      return "辅助工具";
    default:
      return "硬件监控";
  }
}

export function migrationRecordDetails(record: SoftwareDataMigrationRecord): UserDetailSection[] {
  return compactUserDetailSections([
    userDetailSection("迁移内容", [
      userDetailItem("软件", record.softwareName || "未命名软件"),
      userDetailItem("内容", migrationKindLabel(record.migrationKind)),
      userDetailItem("保存位置", migrationTargetCategoryLabel(record.targetCategory)),
      userDetailItem("状态", migrationStateLabel(record.state)),
      userDetailItem("创建时间", userFacingDateTime(record.createdAt)),
      record.restoredAt ? userDetailItem("恢复时间", userFacingDateTime(record.restoredAt)) : null
    ]),
    userDetailSection("位置", [
      userDetailItem("原位置", record.sourcePath),
      userDetailItem("迁移后位置", record.destinationPath)
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
  return text && !/[。！？.!?]$/.test(text) ? `${text}。` : text;
}
