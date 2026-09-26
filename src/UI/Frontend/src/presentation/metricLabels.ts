import { uiText } from "../text.ts";
import {
  formatBytePair,
  formatBytes,
  formatBytesPerSecond,
  type ByteQuantityKind
} from "./byteUnits.ts";
import type { MetricValue, ResourceTableValue } from "../types.ts";

// 后端按稳定指标 id 提供事实，用户看到的名称由当前语言的文案包决定。
// 不认识的 id 保留后端标签，保证新增指标不会显示空白。
export function localizedMetricLabel(
  id: string | null | undefined,
  fallbackLabel?: string | null
): string {
  const normalized = String(id ?? "").trim();
  if (!normalized) {
    return fallbackLabel?.trim() || "";
  }

  const { pattern, index } = normalizeMetricId(normalized);
  const entry = (uiText.metricLabel as Record<string, unknown>)[pattern];
  if (typeof entry === "string") {
    return entry;
  }
  if (typeof entry === "function") {
    return (entry as (value: string) => string)(index ?? "");
  }

  return fallbackLabel?.trim() || normalized;
}

/**
 * 资源列表的表头。后端只发列 id，措辞全部在这里按当前语言取。
 * GPU 列的 id 形如 `gpu.0.usage` / `gpu.1.vram`，走指标文案那一套。
 */
export function resourceTableColumnLabel(id: string | null | undefined): string {
  const normalized = String(id ?? "").trim();
  if (!normalized) {
    return "";
  }

  const columns = uiText.resourceTable.columns as Record<string, string | undefined>;
  const known = columns[normalized];
  if (known) {
    return known;
  }

  const gpu = /^gpu\.(\d+)\.(usage|vram)$/i.exec(normalized);
  if (gpu) {
    return gpu[2].toLowerCase() === "vram"
      ? uiText.resourceTable.gpuVramLabel(gpu[1])
      : uiText.resourceTable.gpuUsageLabel(gpu[1]);
  }

  return localizedMetricLabel(normalized);
}

/**
 * 一条软件记录该叫什么。后端只在有「软件自己的名字」时才发名字：
 * 只代表一个分组的行（Windows 系统、Windows 服务、未归属进程…）名字是空的，
 * 由这里按分组标识出名；资源管理器自己和系统保留那条各有自己的来源。
 *
 * 资源列表和资源占用条都用它，措辞只有这一个出处。
 */
export function softwareDisplayName(software: {
  softwareId?: string | null;
  name?: string | null;
  displayKind?: string | null;
}): string {
  if (software.softwareId === "resource-manager:self") {
    return uiText.shell.productName;
  }

  const residualMetricId = /^resource-residual:(.+)$/.exec(software.softwareId ?? "")?.[1];
  if (residualMetricId) {
    return uiText.resourceTable.systemResidualRow(localizedMetricLabel(residualMetricId));
  }

  const reported = software.name?.trim();
  if (reported) {
    return reported;
  }

  const group = (software.displayKind ?? "").trim() as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[group] ?? "";
}

function normalizeMetricId(id: string): { pattern: string; index: string | null } {
  const segments = id.split(".");
  let index: string | null = null;
  const pattern = segments
    .map((segment) => {
      if (/^\d+$/.test(segment)) {
        index = segment;
        return "{index}";
      }

      const suffixed = /^([a-zA-Z]+)(\d+)$/.exec(segment);
      if (suffixed) {
        index = suffixed[2];
        return `${suffixed[1]}{index}`;
      }

      return segment;
    })
    .join(".");
  return { pattern, index };
}


/**
 * 一条指标读数怎么显示。
 *
 * 后端对容量类指标只发原始字节（unit 为 "B"），换算和单位标签在这里按当前进制模式给出；
 * 其余指标的显示串仍由后端给定（°C、%、MHz、RPM 这类没有进制歧义）。
 * 所有消费端都走这一个函数，不要各自判断 unit。
 */
export function metricDisplayValue(
  metric: MetricValue | null | undefined,
  fallback = "--"
): string {
  if (!metric) {
    return fallback;
  }
  if (metric.unit !== "B") {
    return metric.displayValue || fallback;
  }

  const used = metric.numericValue;
  if (typeof used !== "number" || !Number.isFinite(used)) {
    return fallback;
  }
  const kind = byteQuantityKindForMetric(metric.id);
  return typeof metric.total === "number" && Number.isFinite(metric.total) && metric.total > 0
    ? formatBytePair(used, metric.total, kind)
    : formatBytes(used, kind);
}

/**
 * 一条指标的字节值属于哪一类物理量。
 *
 * 内存、虚拟内存、显存以及它们的占用分解都住在内存颗粒里，是二进制天性；
 * 磁盘读写、网络流量这类累计字节是十进制天性。认不出的一律当存储类，
 * 因为绝大多数新增的字节指标都是流量或容量。
 */
export function byteQuantityKindForMetric(
  metricId: string | null | undefined
): ByteQuantityKind {
  const id = String(metricId ?? "").toLowerCase();
  return id.includes("memory") || id.includes("vram") ? "memory" : "storage";
}

/**
 * 资源表一个单元格显示什么。
 *
 * 单元格只有两种：带单位的数值单元，和纯文本单元（进程号、用户、架构）。
 * 后端对数值单元只发 value 和 unit，不发文本；文本单元反过来只发文本、单位为空。
 * 读不到时后端给 availability，这里统一出「无数据」。
 */
export function resourceTableCellText(
  cell: ResourceTableValue | null | undefined,
  metricId?: string | null
): string {
  if (!cell) {
    return "--";
  }
  if (!cell.unit) {
    return cell.displayValue || "--";
  }
  if (typeof cell.value !== "number" || !Number.isFinite(cell.value)) {
    return cell.availability ? "N/A" : "--";
  }

  switch (cell.unit) {
    case "%":
      return `${cell.value.toFixed(1)}%`;
    case "B":
      return formatBytes(cell.value, byteQuantityKindForMetric(metricId));
    case "B/s":
      return formatBytesPerSecond(cell.value);
    default:
      return `${cell.value.toFixed(1)} ${cell.unit}`.trim();
  }
}
