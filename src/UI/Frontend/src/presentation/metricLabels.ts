import { uiText } from "../text.ts";
import {
  formatBytePair,
  formatBytes,
  formatBytesPerSecond,
  type ByteQuantityKind
} from "./byteUnits.ts";
import { backendMessageDomains } from "./backendMessage.ts";
import { userFacingMetricUnavailableReason } from "./userFacingText.ts";
import { formatFieldValue } from "./fieldValueFormat.ts";
import { formatLegacyMetricNumber } from "./legacyMetricNumber.ts";
import type { MetricDefinition, MetricValue, ResourceProcessSegment, ResourceTableRow, ResourceTableValue } from "../types.ts";

// 后端按稳定指标 id 提供事实，用户看到的名称由当前语言的文案包决定。
// 不认识的 id 由前端适配器提供字段名，保证新增指标不会显示空白。
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

/** 指标的量纲，以及设备总量与软件/进程分项的含义。 */
export function resourceMetricBasis(id: string | null | undefined, surface: "bar" | "table" = "bar"): string | undefined {
  if (id === "disk" || id === "disk.read" || id === "disk.write" || id === "disk.io") {
    return uiText.resourceBreakdown.diskBasis;
  }
  if (id === "memory" || id === "memory.usage" || id === "memory.percent") {
    return surface === "table" ? uiText.resourceBreakdown.memoryTableBasis : uiText.resourceBreakdown.memoryBasis;
  }
  if (id === "virtualMemory.usage" || id === "virtualMemory.percent") {
    return uiText.resourceBreakdown.commitBasis;
  }
  if (/^gpu\.\d+\.vram(?:Percent)?$/i.test(id ?? "")) {
    return uiText.resourceBreakdown.vramBasis;
  }
  if (/^gpu\.\d+\.usage$/i.test(id ?? "")) {
    return uiText.resourceBreakdown.gpuUsageBasis;
  }
  return undefined;
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

/** Synthetic attribution names belong to the UI; real process names remain factual. */
export function resourceProcessDisplayName(
  process: Pick<ResourceProcessSegment, "name" | "attributionKind">,
  metricId: string
): string {
  const text = uiText.resourceBreakdown;
  if (process.attributionKind === "system-residual") {
    if (/^gpu\.\d+\.vram$/.test(metricId)) return text.residualVram;
    if (metricId === "memory.usage") return text.residualMemory;
    if (metricId === "virtualMemory.usage") return text.residualCommit;
    return text.residualSystem;
  }
  if (process.attributionKind === "etw-residual-process") {
    return text.etwResidualProcess(process.name);
  }
  return process.name;
}

/** Process rows already carry the localized fact projection; only software rows use group naming. */
export function resourceTableRowName(row: ResourceTableRow | undefined): string {
  if (row?.kind === "summary") return uiText.resourceTable.summaryRow.name;
  if (row?.kind === "process") return row.name;
  return softwareDisplayName({ softwareId: row?.softwareId, name: row?.name, displayKind: row?.status });
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
 * 后端对容量类指标只发原始字节（unit 为 "B"），字节速率发原始 B/s（numericValue），
 * 换算和单位标签在这里按当前进制模式给出；
 * 其余原始量也由前端按单位格式化。displayValue 只用于前端组合的设备字段卡片。
 * 所有消费端都走这一个函数，不要各自判断 unit。
 */
export function metricDisplayValue(
  metric: MetricValue | null | undefined,
  fallback = "--"
): string {
  if (!metric) {
    return fallback;
  }
  if (metric.status !== undefined && metric.status !== 1) {
    return fallback;
  }
  const numericValue = metric.integerValue !== null && metric.integerValue !== undefined
    ? Number(metric.integerValue)
    : metric.numericValue;
  if (metric.unit === "B/s") {
    return formatBytesPerSecond(numericValue, undefined, fallback);
  }
  if (metric.unit !== "B") {
    if (metric.status === undefined && metric.displayValue) return metric.displayValue;
    // Preserve the established alias precision while canonical field cards keep their own format.
    if (typeof numericValue === "number" && Number.isFinite(numericValue)
      && /^(?:cpu|gpu|memory|virtualMemory|disk|network|system|fan|target)\./.test(metric.id ?? "")) {
      const rounded = (digits: 0 | 1 | 2 | 3) => formatLegacyMetricNumber(numericValue, digits);
      switch (metric.unit) {
        case "": return rounded(2);
        case "%": return `${rounded(1)}%`;
        case "MHz": case "RPM": return `${rounded(0)} ${metric.unit}`;
        case "°C": case "A": case "W": return `${rounded(1)} ${metric.unit}`;
        case "V": return `${rounded(3)} V`;
      }
    }
    return formatFieldValue({
      name: metric.fieldName ?? "",
      unit: metric.unit ?? "",
      valueKind: metric.valueKind ?? "",
      reading: null,
      numericValue: metric.numericValue ?? null,
      integerValue: metric.integerValue === null || metric.integerValue === undefined ? null : BigInt(metric.integerValue),
      booleanValue: metric.booleanValue ?? null,
      observedAt: metric.observedAt ?? null
    }, byteQuantityKindForMetric(metric.id)) ?? fallback;
  }

  const used = numericValue;
  if (typeof used !== "number" || !Number.isFinite(used)) {
    return fallback;
  }
  const kind = byteQuantityKindForMetric(metric.id);
  return typeof metric.total === "number" && Number.isFinite(metric.total) && metric.total > 0
    ? formatBytePair(used, metric.total, kind)
    : formatBytes(used, kind);
}

/**
 * 监视卡片上一个槽位显示什么。
 *
 * 有值：数值和单位分开（单位小一号），两段拼起来与 metricDisplayValue 的结果逐字相同。
 * 没有值：一句灰色短话，原因放悬停说明。没有值分四种：
 * - 目录里没有这一项：「--」；
 * - 需要装组件（目录给出的不可选原因是缺组件）：「需装组件」，说明写缺哪个组件；
 * - 设备不在（设备字段卡片的设备不在字段目录里）：生产端写好的「设备不在」和说明；
 * - 有这一项但没读数：后端的「N/A」或设备字段的「不可用」，说明写目录给的原因或「暂时没有读数」。
 */
export interface MetricCardReading {
  /** 有值时是数值部分；没有值时是那句短话。 */
  value: string;
  /** 数值与单位之间原有的空白。 */
  gap: string;
  /** 单位；没有单位或没有值时为空串。 */
  unit: string;
  missing: boolean;
  /** 悬停说明。 */
  title?: string;
}

// 后端用这几个显示串表示没有读数（与后端 MetricCatalog.IsMetricSelectable 同一口径）。
const noReadingDisplays = new Set(["", "-", "--", "N/A"]);
// 后端 BackendMessageCodes.Metric.NeedsComponent。
const metricNeedsComponentCode = 2;

export function metricCardReading(
  definition: MetricDefinition | null | undefined,
  metric: MetricValue | null | undefined
): MetricCardReading {
  const text = uiText.dashboard;
  const display = metricDisplayValue(metric, "");
  const missing = (value: string, title?: string): MetricCardReading =>
    ({ value, gap: "", unit: "", missing: true, title });

  if (display === text.deviceAbsent) {
    return missing(display, metric?.detail ?? undefined);
  }
  if (!noReadingDisplays.has(display) && display !== text.valueUnavailable) {
    // 值不在目录里时（例如目录还没到），说明沿用读数自带的来源说明。
    return { ...splitMetricUnit(display), missing: false, title: definition ? undefined : metric?.detail ?? undefined };
  }
  if (!definition) {
    return metric
      ? missing(display || "N/A", metric.detail ?? undefined)
      : missing("--", text.notInCatalogHint);
  }
  const reason = definition.disabledReason;
  const reasonText = reason
    ? userFacingMetricUnavailableReason(definition.requiredComponentName, reason)
    : undefined;
  if (reason?.domain === backendMessageDomains.metric && reason.code === metricNeedsComponentCode) {
    return missing(text.needsComponent, reasonText);
  }
  return missing(display || "N/A", reasonText ?? text.noReadingHint);
}

/** 把格式化好的显示串拆成数值和单位：单位是末尾紧贴的「%」，或一个空格后不含数字的一段。 */
export function splitMetricUnit(display: string): Pick<MetricCardReading, "value" | "gap" | "unit"> {
  const match = /^(.*\d)(?:(%)|(\s)([^\d\s][^\d]*))$/.exec(display);
  return match
    ? { value: match[1], gap: match[3] ?? "", unit: match[2] ?? match[4] }
    : { value: display, gap: "", unit: "" };
}

/** 选择器里每项右侧的单位提示：字节类统一写 B，内部计数类不写。 */
export function metricUnitLabel(unit: string | null | undefined): string {
  const normalized = (unit ?? "").trim();
  if (normalized === "MiB" || normalized === "512000 B") {
    return "B";
  }
  return unitlessTokens.has(normalized) ? "" : normalized;
}

const unitlessTokens = new Set([
  "", "1", "ratio", "count", "enum", "flag", "generation", "lanes", "bitmask64", "relative-unit", "relative-unit/h"
]);

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
 * 表格由前端从资源事实生成；文本单元使用本地投影的 displayValue，数值在这里格式化。
 * 读不到时根据本地单元的 availability 显示无数据。
 */
export function resourceTableCellText(
  cell: ResourceTableValue | null | undefined,
  metricId?: string | null
): string {
  if (!cell) {
    return "--";
  }
  if (!cell.unit) {
    if (cell.displayValue) return cell.displayValue;
    return typeof cell.value === "number" && Number.isFinite(cell.value)
      ? String(cell.value)
      : "--";
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
