import { formatFixedDigits, formatSignificantDigits } from "./interfaceNumbers.ts";
import { isReservedSegment } from "../features/resourceBreakdown/resourceProcessIdentity.ts";
import { resourceUnavailableReasonText } from "../features/resourceBreakdown/resourceBarUnavailableReason.ts";
import { uiText } from "../text.ts";
import { isPhysicalMemoryResidualKind, physicalMemoryResidualKindName } from "./physicalMemoryResidual.ts";
import {
  formatBytePair,
  formatBytes,
  formatBytesPerSecond,
  type ByteQuantityKind
} from "./byteUnits.ts";
import { backendMessageDomains } from "./backendMessage.ts";
import { resourceTableCellHeat } from "../features/resourceTable/resourceTableHeat.ts";
import { userFacingMetricUnavailableReason } from "./userFacingText.ts";
import { formatFieldValue } from "./fieldValueFormat.ts";
import { formatLegacyMetricNumber } from "./legacyMetricNumber.ts";
import type { MetricDefinition, MetricValue, ResourceProcessSegment, ResourceTableRow, ResourceTableValue } from "../types.ts";

// Stable backend metric IDs map to labels in the current language.
// Unknown IDs use adapter-provided field names so new metrics retain a label.
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

/** Localized resource-table headings from column IDs; GPU IDs such as gpu.0.usage use metric labels. */
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

  return uiText.resourceTable.processFieldNames[normalized] ?? localizedMetricLabel(normalized);
}

/** Metric dimension and meanings of whole-device and software/process parts. */
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

/** Shared software names for tables and bars: factual names when present, otherwise localized group labels. RM and system-reserved rows have dedicated identities. */
export function softwareDisplayName(software: {
  softwareId?: string | null;
  name?: string | null;
  displayKind?: string | null;
}, totalMetricId: string | null): string {
  if (isReservedSegment(software)) {
    return uiText.resourceTable.systemResidualRow(localizedMetricLabel(totalMetricId));
  }

  if (software.softwareId === "resource-manager:self") {
    return uiText.shell.productName;
  }

  const reported = software.name?.trim();
  if (reported) {
    return reported;
  }

  if (software.displayKind === "system-residual") return uiText.resourceBreakdown.residualSystem;

  const group = (software.displayKind ?? "").trim() as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[group] ?? "";
}

/** Synthetic attribution names belong to the UI; real process names remain factual. */
export function resourceProcessDisplayName(
  process: Pick<ResourceProcessSegment, "name" | "attributionKind">,
  metricId: string | null
): string {
  const text = uiText.resourceBreakdown;
  if (process.attributionKind === "system-residual") {
    if (/^gpu\.\d+\.vram$/.test(metricId ?? "")) return text.residualVram;
    if (metricId === "memory.usage") return text.residualMemory;
    if (metricId === "virtualMemory.usage") return text.residualCommit;
    return text.residualSystem;
  }
  if (process.attributionKind === "etw-residual-process") {
    return text.etwResidualProcess(process.name);
  }
  // Physical-memory residual rows (PID -1) use attribution-kind labels, never backend name.
  if (isPhysicalMemoryResidualKind(process.attributionKind)) {
    return physicalMemoryResidualKindName(process.attributionKind);
  }
  return process.name;
}

/** Process rows already carry the localized fact projection; only software rows use group naming. */
export function resourceTableRowName(row: ResourceTableRow | undefined): string {
  if (row?.kind === "summary") return uiText.resourceTable.summaryRow.name;
  if (row?.kind === "process" || row?.kind === "attribution" || row?.kind === "filtered") return row.name;
  return softwareDisplayName({ softwareId: row?.softwareId, name: row?.name, displayKind: row?.displayKind }, row?.totalMetricId ?? null);
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


/** Format raw numeric facts by unit through one shared path. Byte capacities and B/s use the current radix; displayValue is reserved for locally composed field cards. */
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

/** Card slot text: separate value and unit without changing the formatted string. Missing catalog entries show a placeholder; missing components and devices show their reason; unread metrics use localized no-reading copy. Tooltips explain unavailable values. */
export interface MetricCardReading {
  /** Numeric text when available, otherwise a short status. */
  value: string;
  /** Original whitespace between value and unit. */
  gap: string;
  /** Unit, or empty when absent or unavailable. */
  unit: string;
  missing: boolean;
  /** Hover explanation. */
  title?: string;
}

// Backend Metric.NeedsComponent code.
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
  if (display !== "" && display !== text.valueUnavailable) {
    // Before a catalog entry arrives, retain the reading's source explanation.
    return { ...splitMetricUnit(display), missing: false, title: definition ? undefined : metric?.detail ?? undefined };
  }
  if (!definition) {
    return metric
      ? missing(noReadingText(display), metric.detail ?? undefined)
      : missing("--", text.notInCatalogHint);
  }
  const reason = definition.disabledReason;
  const reasonText = reason
    ? userFacingMetricUnavailableReason(definition.requiredComponentName, reason)
    : undefined;
  if (reason?.domain === backendMessageDomains.metric && reason.code === metricNeedsComponentCode) {
    return missing(text.needsComponent, reasonText);
  }
  return missing(noReadingText(display), reasonText ?? text.noReadingHint);
}

/** Keep local device-field unavailable text; missing raw readings use the current language. */
function noReadingText(display: string) {
  return display === uiText.dashboard.valueUnavailable ? display : uiText.dashboard.noReading;
}

/** Split a formatted value from a trailing percent sign or a space-separated unit without digits. */
export function splitMetricUnit(display: string): Pick<MetricCardReading, "value" | "gap" | "unit"> {
  const match = /^(.*\d)(?:(%)|(\s)([^\d\s][^\d]*))$/.exec(display);
  return match
    ? { value: match[1], gap: match[3] ?? "", unit: match[2] ?? match[4] }
    : { value: display, gap: "", unit: "" };
}

/** Picker unit hint: B for byte quantities, empty for internal counts. */
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

/** Memory, virtual memory, VRAM, and their breakdowns use binary native units. Disk/network byte quantities and unknown metrics use storage units. */
export function byteQuantityKindForMetric(
  metricId: string | null | undefined
): ByteQuantityKind {
  const id = String(metricId ?? "").toLowerCase();
  return id.includes("memory") || id.includes("vram") ? "memory" : "storage";
}

/** Localized CPU topology/residency reasons; preserve unknown codes verbatim. */
export function resourceFactUnavailableText(reason: string): { label: string; hint: string } {
  return uiText.resourceTable.unavailableReasons[reason] ?? { label: reason, hint: reason };
}

/** Format projected table cells as numeric values with units or text. Unavailable cells use availability and known reason labels; raw reason codes appear only in tooltips. */
export function resourceTableCellText(
  cell: ResourceTableValue | null | undefined,
  metricId?: string | null
): string {
  const current = resourceTableCurrentCellText(cell, metricId);
  return cell?.limit !== undefined && cell.limit !== null
    ? `${current}/${resourceTableCurrentCellText({ value: cell.limit, unit: cell.unit }, metricId)}` : current;
}

function resourceTableCurrentCellText(
  cell: ResourceTableValue | null | undefined,
  metricId?: string | null
): string {
  if (!cell) {
    return "--";
  }
  if (cell.displayValue != null && (cell.displayValue !== "" || cell.unit === "text")) return cell.displayValue;
  if (cell.integerValue != null && (cell.unit === "count" || cell.unit === "pid")) return cell.integerValue;
  if (!cell.unit) {
    if (cell.displayValue) return cell.displayValue;
    return typeof cell.value === "number" && Number.isFinite(cell.value)
      ? formatSignificantDigits(cell.value, 21)
      : "--";
  }
  if (typeof cell.value !== "number" || !Number.isFinite(cell.value)) {
    if (cell.unavailableReason) return resourceUnavailableReasonText(cell.unavailableReason) ?? uiText.dashboard.noReading;
    return cell.availability ? uiText.dashboard.noReading : "--";
  }

  switch (cell.unit) {
    case "%":
      return `${formatFixedDigits(cell.value, 1)}%`;
    case "B":
      return formatBytes(cell.value, byteQuantityKindForMetric(metricId));
    case "B/s":
      return formatBytesPerSecond(cell.value);
    default:
      return `${formatFixedDigits(cell.value, 1)} ${cell.unit}`.trim();
  }
}

/** Tooltip reason: localized explanation if not already in the cell, followed by the raw code. */
function unavailableReasonLines(code: string, display: string): string {
  const reason = resourceUnavailableReasonText(code);
  return (reason && reason !== display ? `\n${reason}` : "") + `\n${uiText.backendMessage.reasonCode(code)}`;
}

export interface ResourceTableCellPresentation {
  readonly display: string;
  readonly title: string;
  readonly heat: number;
  readonly privateHeat: number;
  readonly unavailable: boolean;
}

/** Shared text, tooltip, and heat projection; heat is this cell's share of heatMaximum. */
export function resourceTableCellPresentation(
  cell: ResourceTableValue | null | undefined,
  columnId: string,
  text: { readonly label: string; readonly basis: string | undefined },
  heatMaximum: number | undefined
): ResourceTableCellPresentation {
  const display = resourceTableCellText(cell, columnId);
  const title = (cell?.sharedValue != null
    ? uiText.resourceTableView.memoryBreakdown(text.label, display,
      formatBytes((cell.value ?? 0) - cell.sharedValue, "memory"), formatBytes(cell.sharedValue, "memory"))
      + `\n${uiText.resourceTableView.heatSharedLegend}`
    : cell ? `${text.label}: ${display}` : text.label)
    + (text.basis ? `\n${text.basis}` : "")
    + (cell?.basis ? `\n${cell.basis}` : "")
    + (cell?.unavailableReason ? unavailableReasonLines(cell.unavailableReason, display) : "");
  const { heat, privateHeat } = resourceTableCellHeat(cell, heatMaximum);
  return { display, title, heat, privateHeat, unavailable: cell?.availability === "Unavailable" };
}
