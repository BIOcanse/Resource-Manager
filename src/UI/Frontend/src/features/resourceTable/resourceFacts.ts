import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import type { ResourceBreakdownBar, ResourceProcessSegment, ResourceSoftwareSegment, ResourceMonitorSnapshot, ResourceTableColumnSettings, ResourceTableRow, ResourceTableSnapshot, ResourceTableValue, ResourceTableViewMode } from "../../types.ts";
import { requireArray, requireOneOf, requireFiniteNumber, requireNonEmptyString, requireNullable, requireRecord, requireSafeInteger, requireString, ResponseDecodeError } from "../../frontendRuntime/request/ResponseDecoder.ts";
import { createMemo, type Accessor } from "solid-js";
import { resourceProcessDisplayName } from "../../presentation/metricLabels.ts";
import { isResourceTableMetricColumn, resourceTableMetricId } from "./resourceTableSources.ts";

type IdentityProcess = Omit<ResourceProcessSegment, "value" | "systemPercent" | "softwarePercent">;
type IdentitySoftware = Omit<ResourceSoftwareSegment, "value" | "systemPercent" | "processes" | "baseScore"> & { processes: IdentityProcess[] };

/**
 * 资源事实的一个指标。totalValue/capacityValue 是 totalMetricId 指向的整机事实（如私有提交量对应系统提交量），
 * 不是进程之和；processSumValue 是本帧有值的真实进程之和（不含系统残差）。只有进程字段指标带这两项，其余为 null。
 * processFieldName 是这一指标实际读的进程字段：自动的 memory.usage 写私有工作集、virtualMemory.usage 写私有提交量，
 * 按字段请求的写它自己；不是进程字段的指标为 null。
 */
export interface ResourceFactsMetric extends ResourceBreakdownBar {
  totalMetricId: string | null;
  processSumValue: number | null;
  processFieldName: string | null;
}

export interface ResourceFacts {
  identities?: IdentitySoftware[];
  capturedAt?: string;
  metrics: ResourceFactsMetric[];
}

/** Decode the neutral attribution catalogs; all table identities are created below. */
export function decodeResourceFacts(value: unknown): ResourceMonitorSnapshot {
  const frame = requireRecord(value);
  if (requireSafeInteger(frame.version, "$.version") !== 2) throw new ResponseDecodeError("$.version", "resource facts version 2");
  const capturedAt = requireNullable(frame.capturedAt, "$.capturedAt", requireString) ?? undefined;
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
      totalMetricId: requireNullable(metric.totalMetricId, `${path}.totalMetricId`, requireNonEmptyString),
      processSumValue: nullableNumber(metric, "processSumValue", path),
      ...(metric.residualSource === undefined || metric.residualSource === null ? {}
        : { residualSource: requireOneOf(metric.residualSource, `${path}.residualSource`, ["counters"] as const) }),
      processFieldName: requireNullable(metric.processFieldName, `${path}.processFieldName`, requireNonEmptyString),
      totalValue: nullableNumber(metric, "totalValue", path), capacityValue: nullableNumber(metric, "capacityValue", path),
      totalSystemPercent: nullableNumber(metric, "totalSystemPercent", path), sharedValue: nullableNumber(metric, "sharedValue", path),
      unavailableReason: metric.unavailableReason == null ? null
        : requireNonEmptyString(metric.unavailableReason, `${path}.unavailableReason`),
      software: requireArray(metric.software, `${path}.software`).map((value, index) => {
        const childPath = `${path}.software[${index}]`;
        const child = requireRecord(value, childPath);
        const identity = software.get(requireString(child.softwareId, `${childPath}.softwareId`));
        if (!identity) throw new ResponseDecodeError(`${childPath}.softwareId`, "known software identity");
        return { ...identity, value: requireFiniteNumber(child.value, `${childPath}.value`), systemPercent: requireFiniteNumber(child.systemPercent, `${childPath}.systemPercent`),
          processCount: requireSafeInteger(child.processCount, `${childPath}.processCount`), baseScore: requireFiniteNumber(child.baseScore, `${childPath}.baseScore`),
          sharedValue: nullableNumber(child, "sharedValue", childPath),
          processes: requireArray(child.processes, `${childPath}.processes`).map((value, index) => {
            const processPath = `${childPath}.processes[${index}]`;
            const process = requireRecord(value, processPath);
            const identity = processes[requireSafeInteger(process.processIndex, `${processPath}.processIndex`)];
            if (!identity) throw new ResponseDecodeError(`${processPath}.processIndex`, "known process identity");
            return { ...identity, value: requireFiniteNumber(process.value, `${processPath}.value`), systemPercent: requireFiniteNumber(process.systemPercent, `${processPath}.systemPercent`),
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
    group.processCount = group.processes.length;
  }
  const facts = { capturedAt, metrics, identities: [...groups.values()] };
  return { capturedAt, facts, breakdown: { capturedAt, bars: [] }, table: { capturedAt, columns: [], rows: [], sort: { columnId: "impact", direction: "desc" } } };
}

export function projectResourceFacts(facts: ResourceFacts, settings: readonly ResourceTableColumnSettings[], mode: ResourceTableViewMode, sortColumnId: string, direction: "asc" | "desc", locale?: string): ResourceTableSnapshot {
  const options = settings;
  const columns = options.filter(column => column.visible).map(column => {
    const metric = facts.metrics.find(candidate => resourceTableMetricId(column) === candidate.metricId);
    return { ...column, source: column.source ?? null, unit: metric?.unit ?? "", processFieldName: metric?.processFieldName ?? null,
      sortable: true, width: column.width ?? ResourceManager.FrontendPoints.DashboardLayoutLimits.defaultColumnWidth };
  });
  // 列按自己的来源认领指标：自动选择认列原来的指标，选了来源就认那个进程字段。
  const columnByMetric = new Map<string, string>();
  for (const column of options) {
    const metricId = resourceTableMetricId(column);
    if (metricId !== null) columnByMetric.set(metricId, column.id);
  }
  const softwareRows = new Map<string, ResourceTableRow>();
  const processRows = new Map<string, ResourceTableRow>();
  const children = new Map<string, ResourceTableRow[]>();
  const metadata = new Map<string, { ids: Set<number>; names: Set<string>; paths: Set<string> }>();
  const summary = row("summary:total", "summary", "", facts.metrics.length > 0 ? "ready" : "empty");
  const cell = (value: number | null, percent: number | null, unit: string, sharedValue?: number | null): ResourceTableValue => ({ value, percent, unit, displayValue: "", sharedValue });
  function ensureSoftware(software: IdentitySoftware) {
    let group = softwareRows.get(software.softwareId);
    if (!group) {
      group = { ...row(`software:${software.softwareId}`, "software", software.name, software.displayKind), softwareId: software.softwareId, processIds: [], processNames: [], executablePaths: [] };
      softwareRows.set(software.softwareId, group);
      metadata.set(software.softwareId, { ids: new Set(), names: new Set(), paths: new Set() });
    }
    group.processCount = Math.max(group.processCount, software.processCount);
    return group;
  }
  // Residual rows are named from the metric that carries them; identity-only rows have no metric.
  function ensureProcess(software: IdentitySoftware, process: IdentityProcess, metricId = "") {
    const group = ensureSoftware(software);
    const identity = process.processStartKey ? `${process.processId}:${process.processStartKey}` : `${process.attributionKind}:${process.processId}:${process.name}`;
    const key = `${software.softwareId}:${identity}`;
    let child = processRows.get(key);
    if (!child) {
      const processId = process.processId > 0 && process.attributionKind !== "system-residual" ? process.processId : null;
      // 残差进程按它所属的整机事实命名：残差软件 id 就写着这个指标（私有提交量的残差是 resource-residual:virtualMemory.usage），
      // 和软件行的名字同一出处；身份目录先于数值建行时也一样。
      const displayName = resourceProcessDisplayName(process, /^resource-residual:(.+)$/.exec(software.softwareId)?.[1] ?? metricId);
      child = { ...row(`process:${key}`, "process", displayName, process.attributionKind === "system-residual" ? "system-residual" : process.attributionKind === "etw-residual-process" ? "DXGKrnl/VidMm" : "running"),
        softwareId: software.softwareId, softwareName: software.name, parentId: mode === "process" ? null : group.id, depth: mode === "process" ? 0 : 1,
        processId, processStartKey: process.processStartKey, processCount: processId ? 1 : 0, processIds: processId ? [processId] : [], processNames: process.name.trim() ? [process.name] : [], executablePaths: process.executablePath ? [process.executablePath] : [] };
      child.values.pid = { value: processId, displayValue: processId?.toString() ?? "--", unit: "", attributionKind: process.attributionKind };
      child.values.user = { displayValue: process.userName || "--", unit: "" };
      child.values.architecture = { displayValue: process.architecture || "--", unit: "" };
      processRows.set(key, child);
      const siblings = children.get(software.softwareId) ?? [];
      siblings.push(child);
      children.set(software.softwareId, siblings);
      const seen = metadata.get(software.softwareId)!;
      if (processId && !seen.ids.has(processId)) {
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
    for (const process of software.processes) ensureProcess(software, process);
  }
  for (const metric of facts.metrics) {
    const columnId = columnByMetric.get(metric.metricId) ?? null;
    if (columnId) summary.values[columnId] = metric.totalValue !== null && metric.totalSystemPercent !== null && metric.capacityValue !== null
      ? cell(metric.totalValue, metric.totalSystemPercent, metric.unit, metric.sharedValue)
      : { ...cell(null, null, metric.unit), availability: "Unavailable" };
    for (const software of metric.software) {
      const group = ensureSoftware(software);
      if (columnId) group.values[columnId] = cell(software.value, software.systemPercent, metric.unit, software.sharedValue);
      for (const process of software.processes) {
        const child = ensureProcess(software, process, metric.metricId);
        if (columnId) child.values[columnId] = cell(process.value, process.systemPercent, metric.unit, process.sharedValue);
      }
    }
  }
  const weights: Record<string, number> = { cpu: 1, memory: .9, disk: .45, network: .25 };
  const visibleIds = new Set(columns.map(column => column.id));
  for (const item of [summary, ...softwareRows.values(), ...processRows.values()]) {
    item.impactScore = Object.entries(item.values).reduce((sum, [key, value]) => sum + (value.percent ?? 0) * (weights[key] ?? (key.startsWith("gpu.") ? 1 : 0)), 0);
    // Identity text remains searchable, while numeric cells follow local visibility.
    for (const key of Object.keys(item.values)) if (isResourceTableMetricColumn(key) && !visibleIds.has(key)) delete item.values[key];
  }
  // CurrentCultureIgnoreCase distinguishes accents, ignores case and leaves equal keys stable.
  // Digit runs compare by value, as Explorer orders names: "App 2" comes before "App 10".
  const textOrder = new Intl.Collator(locale, { sensitivity: "accent", numeric: true });
  const compare = (textKeys: boolean) => (left: ResourceTableRow, right: ResourceTableRow) => {
    const key = (item: ResourceTableRow): string | number => textKeys && sortColumnId === "name" ? item.name : textKeys && sortColumnId === "status" ? item.status : sortColumnId === "impact" ? item.impactScore : textKeys && ["user", "architecture"].includes(sortColumnId) ? item.values[sortColumnId]?.displayValue ?? "" : item.values[sortColumnId]?.value ?? -Number.MAX_VALUE;
    const a = key(left), b = key(right);
    const result = typeof a === "string" && typeof b === "string" ? textOrder.compare(a, b) : a === b ? 0 : a < b ? -1 : 1;
    return result * (direction === "asc" ? 1 : -1) || textOrder.compare(left.name, right.name);
  };
  const rows = mode === "process" ? [...processRows.values()].sort(compare(true)) : [...softwareRows.values()].sort(compare(true)).flatMap(group => [group, ...(children.get(group.softwareId!) ?? []).sort(compare(false))]);
  return { capturedAt: facts.capturedAt, columns, rows: [summary, ...rows], sort: { columnId: sortColumnId, direction }, viewMode: mode };
}

/** Widths are consumed by the table's column memo, independently of fact rows. */
export function createResourceFactsProjection(
  facts: Accessor<ResourceFacts | null>,
  settings: Accessor<readonly ResourceTableColumnSettings[]>,
  mode: Accessor<ResourceTableViewMode>,
  sort: Accessor<{ columnId: string; direction: "asc" | "desc" }>,
  project: typeof projectResourceFacts = projectResourceFacts
): Accessor<ResourceTableSnapshot | null> {
  const columns = createMemo(() => settings().map(({ id, visible, source }) => ({ id, visible, source: source ?? null })), undefined, {
    equals: (previous, next) => previous.length === next.length
      && previous.every((column, index) => column.id === next[index].id && column.visible === next[index].visible
        && column.source === next[index].source)
  });
  return createMemo(() => {
    const current = facts();
    const order = sort();
    return current ? project(current, columns(), mode(), order.columnId, order.direction) : null;
  });
}

function row(id: string, kind: string, name: string, status: string): ResourceTableRow {
  return { id, kind, name, status, depth: 0, processCount: 0, impactScore: 0, values: {} };
}
