import { formatTrimmedDigits } from "../../presentation/interfaceNumbers.ts";
import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import type { ResourceBreakdownBar, ResourceProcessSegment, ResourceSoftwareSegment, ResourceFactsMonitorSnapshot, ResourceTableColumnSettings, ResourceTableRow, ResourceTableSnapshot, ResourceTableValue, ResourceTableViewMode } from "../../types.ts";
import { decodeFactsMetricFacts, requireTimestamp } from "../../api/resourceMetricFactsWire.ts";
import { requireArray, requireOneOf, requireFiniteNumber, requireNonEmptyString, requireNullable, requireRecord, requireSafeInteger, requireString, ResponseDecodeError } from "../../frontendRuntime/request/ResponseDecoder.ts";
import { createMemo, type Accessor } from "solid-js";
import { isReservedSegment } from "../resourceBreakdown/resourceProcessIdentity.ts";
import { resourceProcessDisplayName, resourceTableCellText } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import { isResourceTableMetricColumn, resourceTableMetricId } from "./resourceTableSources.ts";
import { resourceTableColumnsEqual } from "./resourceTableColumnIdentity.ts";
import { decodeResourceFieldFacts, type ResourceFieldFacts } from "./resourceFieldFacts.ts";

type IdentityProcess = Omit<ResourceProcessSegment, "value" | "systemPercent" | "softwarePercent">;
type IdentitySoftware = Omit<ResourceSoftwareSegment, "value" | "systemPercent" | "processes" | "baseScore"> & { processes: IdentityProcess[] };

/**
 * 后端的进程身份规则（ResourceFactsSnapshot.Index）：PID > 0 的是进程，按 PID 和启动标识认；
 * 其余是归属叶子（系统/驱动保留下的分项，PID -1、名字可为空），按 attributionKind 和名字认。叶子不是进程，
 * 不计进程数、不显示 PID。
 */
function isProcess(process: Pick<ResourceProcessSegment, "processId">): boolean {
  return process.processId > 0;
}

/**
 * 资源事实的一个指标，带它自己的整机事实（ResourceMetricFacts）：total、processSum、带符号的 residual、
 * 时刻、窗口、口径和不可用原因都只属于这个指标，不和别的指标或别的订阅混用。
 * 普通资源指标的 totalMetricId 就是它自己的 metricId，进程字段指标指向它比较的整机指标。
 * 残差是 displayKind 为 system-residual 的软件条目（软件 id 不透明），它下面的叶子是归属行。
 */
export type ResourceFactsMetric = ResourceBreakdownBar;

export interface ResourceFacts {
  fields?: ResourceFieldFacts[];
  identities?: IdentitySoftware[];
  capturedAt?: string;
  metrics: ResourceFactsMetric[];
}

/** Decode the neutral attribution catalogs; all table identities are created below. */
export function decodeResourceFacts(value: unknown): ResourceFactsMonitorSnapshot {
  const frame = requireRecord(value);
  if (requireSafeInteger(frame.version, "$.version") !== 2) throw new ResponseDecodeError("$.version", "resource facts version 2");
  const capturedAt = requireNullable(frame.capturedAt, "$.capturedAt", requireTimestamp) ?? undefined;
  const software = new Map(requireArray(frame.software, "$.software").map((value, index) => {
    const path = `$.software[${index}]`;
    const item = requireRecord(value, path);
    const softwareId = requireNonEmptyString(item.softwareId, `${path}.softwareId`);
    return [softwareId, { softwareId, name: requireString(item.name, `${path}.name`),
      kind: requireString(item.kind, `${path}.kind`), displayKind: requireString(item.attributionKind, `${path}.attributionKind`) }] as const;
  }));
  const processes = requireArray(frame.processes, "$.processes").map((value, index) => {
    const path = `$.processes[${index}]`;
    const item = requireRecord(value, path);
    const startKey = requireNullable(item.processStartKey, `${path}.processStartKey`, requireString);
    if (startKey !== null && !/^[1-9]\d*$/.test(startKey)) throw new ResponseDecodeError(`${path}.processStartKey`, "positive integer string");
    return { processId: requireSafeInteger(item.processId, `${path}.processId`), processStartKey: startKey,
      name: requireString(item.name, `${path}.name`), executablePath: requireNullable(item.executablePath, `${path}.executablePath`, requireString),
      userName: requireNullable(item.userName, `${path}.userName`, requireString), architecture: requireNullable(item.architecture, `${path}.architecture`, requireString),
      attributionKind: requireString(item.attributionKind, `${path}.attributionKind`) };
  });
  const nullableNumber = (item: Record<string, unknown>, key: string, path: string) => requireNullable(item[key], `${path}.${key}`, requireFiniteNumber);
  const metrics = requireArray(frame.metrics, "$.metrics").map((value, index): ResourceFactsMetric => {
    const path = `$.metrics[${index}]`;
    const metric = requireRecord(value, path);
    return { metricId: requireNonEmptyString(metric.metricId, `${path}.metricId`), unit: requireString(metric.unit, `${path}.unit`), scaleMode: "capacity",
      ...decodeFactsMetricFacts(metric, path),
      residualSource: requireNullable(metric.residualSource, `${path}.residualSource`,
        (source, sourcePath) => requireOneOf(source, sourcePath, ["counters"] as const)),
      processFieldName: requireNullable(metric.processFieldName, `${path}.processFieldName`, requireNonEmptyString),
      capacityValue: nullableNumber(metric, "capacityValue", path),
      totalSystemPercent: nullableNumber(metric, "totalSystemPercent", path), sharedValue: nullableNumber(metric, "sharedValue", path),
      unavailableReason: metric.unavailableReason == null ? null
        : requireNonEmptyString(metric.unavailableReason, `${path}.unavailableReason`),
      software: requireArray(metric.software, `${path}.software`).map((value, index) => {
        const childPath = `${path}.software[${index}]`;
        const child = requireRecord(value, childPath);
        const identity = software.get(requireString(child.softwareId, `${childPath}.softwareId`));
        if (!identity) throw new ResponseDecodeError(`${childPath}.softwareId`, "known software identity");
        return { ...identity, limit: child.limit === undefined ? null : nullableNumber(child, "limit", childPath), value: requireFiniteNumber(child.value, `${childPath}.value`), systemPercent: requireFiniteNumber(child.systemPercent, `${childPath}.systemPercent`),
          processCount: requireSafeInteger(child.processCount, `${childPath}.processCount`), baseScore: requireFiniteNumber(child.baseScore, `${childPath}.baseScore`),
          sharedValue: nullableNumber(child, "sharedValue", childPath),
          processes: requireArray(child.processes, `${childPath}.processes`).map((value, index) => {
            const processPath = `${childPath}.processes[${index}]`;
            const process = requireRecord(value, processPath);
            const identity = processes[requireSafeInteger(process.processIndex, `${processPath}.processIndex`)];
            if (!identity) throw new ResponseDecodeError(`${processPath}.processIndex`, "known process identity");
            return { ...identity, limit: process.limit === undefined ? null : nullableNumber(process, "limit", processPath), value: requireFiniteNumber(process.value, `${processPath}.value`), systemPercent: requireFiniteNumber(process.systemPercent, `${processPath}.systemPercent`),
              softwarePercent: requireFiniteNumber(process.softwarePercent, `${processPath}.softwarePercent`), baseScore: requireFiniteNumber(process.baseScore, `${processPath}.baseScore`), sharedValue: nullableNumber(process, "sharedValue", processPath) };
          }) };
      }) };
  });
  const groups = new Map<string, IdentitySoftware>();
  for (const [index, value] of requireArray(frame.attributions, "$.attributions").entries()) {
    const path = `$.attributions[${index}]`;
    const item = requireRecord(value, path);
    const id = requireNonEmptyString(item.softwareId, `${path}.softwareId`);
    const owner = software.get(id);
    const process = processes[requireSafeInteger(item.processIndex, `${path}.processIndex`)];
    if (!owner || !process) throw new ResponseDecodeError(path, "known software and process identity");
    let group = groups.get(id);
    if (!group) { group = { ...owner, processCount: 0, processes: [] }; groups.set(id, group); }
    group.processes.push({ ...process, baseScore: requireFiniteNumber(item.baseScore, `${path}.baseScore`) });
    if (isProcess(process)) group.processCount++;
  }
  const facts = { capturedAt, metrics, identities: [...groups.values()],
    fields: decodeResourceFieldFacts(frame.fields, processes, new Set(software.keys())) };
  return { scope: "table", capturedAt, facts };
}

/** collationLocale orders the text keys (names, status, user, architecture); it is the interface language. */
export function projectResourceFacts(facts: ResourceFacts, settings: readonly ResourceTableColumnSettings[], mode: ResourceTableViewMode, sortColumnId: string, direction: "asc" | "desc", collationLocale: string): ResourceTableSnapshot {
  const options = settings;
  const columns = options.filter(column => column.visible).map(column => {
    const metric = facts.metrics.find(candidate => resourceTableMetricId(column) === candidate.metricId);
    return { ...column, source: column.source ?? null, unit: metric?.unit ?? "", processFieldName: metric?.processFieldName ?? null,
      additiveAcrossProcesses: metric?.additiveAcrossProcesses ?? null,
      sortable: true, width: column.width ?? ResourceManager.FrontendPoints.DashboardLayoutLimits.defaultColumnWidth };
  });
  // 列按自己的来源认领指标：自动选择认列原来的指标，选了来源就认那个进程字段。
  const columnByMetric = new Map<string, string>();
  for (const column of options) {
    const metricId = resourceTableMetricId(column);
    if (metricId !== null) columnByMetric.set(metricId, column.id);
  }
  // Identity-only catalog entries share the explicit total context of their metric rows.
  const totalMetricBySoftware = new Map(facts.metrics.flatMap(metric => metric.software
    .filter(isReservedSegment).map(software => [software.softwareId, metric.totalMetricId] as const)));
  const softwareRows = new Map<string, ResourceTableRow>();
  const processRows = new Map<string, ResourceTableRow>();
  const attributionRows = new Map<string, ResourceTableRow>();
  const children = new Map<string, ResourceTableRow[]>();
  const metadata = new Map<string, { ids: Set<number>; names: Set<string>; paths: Set<string> }>();
  const summary = row("summary:total", "summary", "", facts.metrics.length > 0 || (facts.fields?.length ?? 0) > 0 ? "ready" : "empty");
  const cell = (value: number | null, percent: number | null, unit: string, sharedValue?: number | null): ResourceTableValue => ({ value, percent, unit, displayValue: "", sharedValue });
  for (const column of columns) if (isResourceTableMetricColumn(column.id)) {
    summary.values[column.id] = { ...cell(null, null, column.unit), displayValue: uiText.dashboard.noReading, availability: "Unavailable" };
  }
  function ensureSoftware(software: IdentitySoftware) {
    let group = softwareRows.get(software.softwareId);
    if (!group) {
      group = { ...row(`software:${software.softwareId}`, "software", software.name, software.displayKind), softwareId: software.softwareId, displayKind: software.displayKind, totalMetricId: totalMetricBySoftware.get(software.softwareId) ?? null, processIds: [], processNames: [], executablePaths: [] };
      softwareRows.set(software.softwareId, group);
      metadata.set(software.softwareId, { ids: new Set(), names: new Set(), paths: new Set() });
    }
    group.processCount = Math.max(group.processCount, software.processCount);
    return group;
  }
  // 归属叶子（PID ≤ 0）不是进程：只在进程视图里成为自己的一行，按后端的软件条目、attributionKind 和名字认，
  // 名字由 resourceProcessDisplayName 按叶子种类给出。软件视图里叶子的值已经在所属条目那一行
  // （后端给这个条目的进程数是 0，不展开）。
  function ensureAttribution(software: IdentitySoftware, process: IdentityProcess, metric: ResourceFactsMetric) {
    const key = `${software.softwareId}:${process.attributionKind}:${process.processId}:${process.name}`;
    let item = attributionRows.get(key);
    if (!item) {
      item = { ...row(`attribution:${key}`, "attribution", resourceProcessDisplayName(process, metric.totalMetricId ?? metric.metricId), software.displayKind),
        softwareId: software.softwareId, displayKind: software.displayKind, totalMetricId: metric.totalMetricId, softwareName: software.name, processId: null, processCount: 0, processIds: [], processNames: [], executablePaths: [] };
      item.values.pid = { value: null, displayValue: "--", unit: "", attributionKind: process.attributionKind };
      attributionRows.set(key, item);
    }
    return item;
  }
  function ensureProcess(software: IdentitySoftware, process: IdentityProcess) {
    const group = ensureSoftware(software);
    const key = `${software.softwareId}:${process.processId}:${process.processStartKey ?? ""}`;
    let child = processRows.get(key);
    if (!child) {
      const processId = process.processId;
      child = { ...row(`process:${key}`, "process", resourceProcessDisplayName(process, null), process.attributionKind === "etw-residual-process" ? "DXGKrnl/VidMm" : "running"),
        softwareId: software.softwareId, displayKind: software.displayKind, softwareName: software.name, parentId: mode === "process" ? null : group.id, depth: mode === "process" ? 0 : 1,
        processId, processStartKey: process.processStartKey, processCount: 1, processIds: [processId], processNames: process.name.trim() ? [process.name] : [], executablePaths: process.executablePath ? [process.executablePath] : [] };
      child.values.pid = { value: processId, displayValue: processId.toString(), unit: "", attributionKind: process.attributionKind };
      child.values.user = { displayValue: process.userName || "--", unit: "" };
      child.values.architecture = { displayValue: process.architecture || "--", unit: "" };
      processRows.set(key, child);
      const siblings = children.get(software.softwareId) ?? [];
      siblings.push(child);
      children.set(software.softwareId, siblings);
      const seen = metadata.get(software.softwareId)!;
      if (!seen.ids.has(processId)) {
        seen.ids.add(processId);
        group.processIds!.push(processId);
      }
      const processName = process.name.toLowerCase();
      if (process.name.trim() && !seen.names.has(processName)) {
        seen.names.add(processName);
        group.processNames!.push(process.name);
      }
      const processPath = process.executablePath?.toLowerCase();
      if (process.executablePath?.trim() && processPath && !seen.paths.has(processPath)) {
        seen.paths.add(processPath);
        group.executablePaths!.push(process.executablePath);
      }
    }
    return child;
  }
  for (const software of facts.identities ?? []) {
    ensureSoftware(software);
    for (const process of software.processes) if (isProcess(process)) ensureProcess(software, process);
  }
  for (const metric of facts.metrics) {
    const columnId = columnByMetric.get(metric.metricId) ?? null;
    if (columnId) summary.values[columnId] = metric.totalValue !== null
      ? { ...cell(metric.totalValue, metric.totalSystemPercent, metric.unit, metric.sharedValue), basis: resourceFactsTotalBasis(metric) }
      : { ...cell(null, null, metric.unit), availability: "Unavailable", unavailableReason: metric.unavailableReason };
    for (const software of metric.software) {
      const group = ensureSoftware(software);
      if (columnId) group.values[columnId] = { ...cell(software.value, software.systemPercent, metric.unit, software.sharedValue), limit: software.limit };
      for (const process of software.processes) {
        if (isProcess(process)) {
          const child = ensureProcess(software, process);
          if (columnId) child.values[columnId] = { ...cell(process.value, process.systemPercent, metric.unit, process.sharedValue), limit: process.limit };
        } else if (mode === "process") {
          const leaf = ensureAttribution(software, process, metric);
          if (columnId) leaf.values[columnId] = cell(process.value, process.systemPercent, metric.unit, process.sharedValue);
        }
      }
    }
  }
  const rowsByProcess = new Map<string, ResourceTableRow[]>();
  for (const child of processRows.values()) {
    const key = `${child.processId}:${child.processStartKey}`;
    const rows = rowsByProcess.get(key) ?? [];
    rows.push(child); rowsByProcess.set(key, rows);
  }
  for (const field of facts.fields ?? []) {
    for (const column of columns.filter(column => column.id.startsWith("process_") && resourceTableMetricId(column) === field.field)) {
      column.unit = field.unit;
      column.processFieldName = field.field;
      column.additiveAcrossProcesses = field.additiveAcrossProcesses;
      for (const value of field.software) {
        const group = softwareRows.get(value.softwareId);
        if (group) group.values[column.id] = value.value;
      }
      for (const value of field.processes) {
        const identity = value.identity;
        for (const child of rowsByProcess.get(`${identity.processId}:${identity.processStartKey}`) ?? []) {
          child.values[column.id] = value.value;
        }
      }
    }
  }
  const weights: Record<string, number> = { cpu: 1, memory: .9, disk: .45, network: .25 };
  const visibleIds = new Set(columns.map(column => column.id));
  for (const item of [summary, ...softwareRows.values(), ...processRows.values(), ...attributionRows.values()]) {
    for (const column of columns) if ((column.source || column.id.startsWith("process_")) && !item.values[column.id]) {
      item.values[column.id] = { ...cell(null, null, column.unit), displayValue: uiText.dashboard.noReading, availability: "Unavailable" };
    }
    item.impactScore = Object.entries(item.values).reduce((sum, [key, value]) => sum + (value.percent ?? 0) * (weights[key] ?? (key.startsWith("gpu.") ? 1 : 0)), 0);
    // Identity text remains searchable, while numeric cells follow local visibility.
    for (const key of Object.keys(item.values)) if (isResourceTableMetricColumn(key) && !visibleIds.has(key)) delete item.values[key];
  }
  // CurrentCultureIgnoreCase distinguishes accents, ignores case and leaves equal keys stable.
  // Digit runs compare by value, as Explorer orders names: "App 2" comes before "App 10".
  const textOrder = new Intl.Collator(collationLocale, { sensitivity: "accent", numeric: true });
  const compare = (textKeys: boolean) => (left: ResourceTableRow, right: ResourceTableRow) => {
    const key = (item: ResourceTableRow): string | number | bigint => {
      if (textKeys && sortColumnId === "name") return item.name;
      if (textKeys && sortColumnId === "status") return item.status;
      if (sortColumnId === "impact") return item.impactScore;
      const cell = item.values[sortColumnId];
      if (cell?.integerValue != null) return BigInt(cell.integerValue);
      return cell?.value ?? cell?.displayValue ?? -Number.MAX_VALUE;
    };
    const a = key(left), b = key(right);
    const result = typeof a === "string" && typeof b === "string" ? textOrder.compare(a, b) : a === b ? 0 : a < b ? -1 : 1;
    return result * (direction === "asc" ? 1 : -1) || textOrder.compare(left.name, right.name);
  };
  const rows = mode === "process" ? [...processRows.values(), ...attributionRows.values()].sort(compare(true)) : [...softwareRows.values()].sort(compare(true)).flatMap(group => [group, ...(children.get(group.softwareId!) ?? []).sort(compare(false))]);
  return { capturedAt: facts.capturedAt, columns, rows: [summary, ...rows], sort: { columnId: sortColumnId, direction }, viewMode: mode };
}

/**
 * 总计格的说明，全部取这个指标自己的事实：可加口径写出进程之和与带符号的残差，不可加口径写明不能相加（没有残差），
 * 有采样窗口时写出窗口。
 */
export function resourceFactsTotalBasis(metric: ResourceFactsMetric): string | undefined {
  const text = uiText.resourceTable;
  const value = (number: number) => resourceTableCellText({ value: number, unit: metric.unit }, metric.totalMetricId ?? metric.metricId);
  const balance = metric.processSumValue !== null && metric.residualValue !== null
    ? text.factsBalance(value(metric.processSumValue), (metric.residualValue < 0 ? "−" : "") + value(Math.abs(metric.residualValue)))
    : null;
  const lines = [metric.additiveAcrossProcesses ? balance : text.nonAdditiveBasis,
    metric.windowMilliseconds !== null ? text.factsWindow(formatTrimmedDigits(metric.windowMilliseconds / 1000, 3)) : null];
  const basis = lines.filter((line): line is string => line !== null).join("\n");
  return basis || undefined;
}

/**
 * Widths are consumed by the table's column memo, independently of fact rows.
 * collationLocale is the interface language: rows are sorted again when it changes.
 */
export function createResourceFactsProjection(
  facts: Accessor<ResourceFacts | null>,
  settings: Accessor<readonly ResourceTableColumnSettings[]>,
  mode: Accessor<ResourceTableViewMode>,
  sort: Accessor<{ columnId: string; direction: "asc" | "desc" }>,
  collationLocale: Accessor<string>,
  project: typeof projectResourceFacts = projectResourceFacts
): Accessor<ResourceTableSnapshot | null> {
  const columns = createMemo(() => settings().map(({ id, visible, source }) => ({ id, visible, source: source ?? null })), undefined, {
    equals: (previous, next) => previous.length === next.length
      && previous.every((column, index) => column.id === next[index].id && column.visible === next[index].visible
        && column.source === next[index].source)
  });
  return createMemo<ResourceTableSnapshot | null>(previous => {
    const current = facts();
    const order = sort();
    if (!current) return null;
    const next = project(current, columns(), mode(), order.columnId, order.direction, collationLocale());
    // Equal columns keep the previous array, so the table's column consumers do not rebuild with every frame.
    return previous && resourceTableColumnsEqual(previous.columns, next.columns) ? { ...next, columns: previous.columns } : next;
  }, null);
}

function row(id: string, kind: string, name: string, status: string): ResourceTableRow {
  return { id, kind, name, status, depth: 0, processCount: 0, impactScore: 0, values: {} };
}
