import type {
  ResourceBreakdownBar,
  ResourceBreakdownSnapshot,
  ResourceMonitorSnapshot,
  ResourceProcessSegment,
  ResourceScaleMode,
  ResourceSoftwareSegment,
  ResourceTableColumn,
  ResourceTableRow,
  ResourceTableSnapshot,
  ResourceTableValue
} from "../types";
import {
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString,
  ResponseDecodeError
} from "../frontendRuntime/request/ResponseDecoder.ts";

type SoftwareIdentityRow = [
  softwareId: string,
  name: string,
  kind: string,
  displayKind: string
];

type ProcessRow = [
  processId: number,
  name: string,
  executablePath: string | null,
  value: number,
  systemPercent: number,
  softwarePercent: number,
  displayValue: string,
  userName: string | null,
  architecture: string | null,
  attributionKind: string,
  baseScore: number,
  processStartKey: string | null
];

type SoftwareValueRow = [
  softwareIndex: number,
  value: number,
  systemPercent: number,
  displayValue: string,
  processCount: number,
  baseScore: number,
  processes: ProcessRow[]
];

interface ResourceBreakdownBarWire {
  metricId: string;
  label: string;
  unit: string;
  scaleMode: ResourceScaleMode;
  totalValue: number | null;
  capacityValue: number | null;
  totalSystemPercent: number | null;
  totalDisplay: string;
  software: SoftwareValueRow[];
}

export interface ResourceBreakdownWireSnapshot {
  version: 7;
  capturedAt: string | null;
  softwareCatalog: SoftwareIdentityRow[];
  bars: ResourceBreakdownBarWire[];
}

export interface ResourceMonitorWireSnapshot {
  version: 8;
  capturedAt: string | null;
  breakdown: ResourceBreakdownWireSnapshot;
  table: ResourceTableWireSnapshot;
}

export interface ResourceTableWireSnapshot extends
  Omit<ResourceTableSnapshot, "capturedAt"> {
  capturedAt: string | null;
}

const scaleModes = ["capacity", "active"] as const;

export function decodeResourceBreakdownWireSnapshot(
  value: unknown
): ResourceBreakdownSnapshot {
  const snapshot = requireRecord(value);
  if (requireSafeInteger(snapshot.version, "$.version") !== 7) {
    throw new ResponseDecodeError("$.version", "resource breakdown wire version 7");
  }

  const softwareCatalog = requireArray(snapshot.softwareCatalog, "$.softwareCatalog")
    .map((row, index) => decodeSoftwareIdentity(row, `$.softwareCatalog[${index}]`));
  const capturedAt = requireNullable(
    snapshot.capturedAt,
    "$.capturedAt",
    requireTimestamp);
  const bars = requireArray(snapshot.bars, "$.bars")
    .map((bar, index) => decodeBar(
      bar,
      softwareCatalog,
      `$.bars[${index}]`));

  return { capturedAt: capturedAt ?? undefined, bars };
}

export function decodeResourceMonitorWireSnapshot(
  value: unknown
): ResourceMonitorSnapshot {
  const snapshot = requireRecord(value);
  if (requireSafeInteger(snapshot.version, "$.version") !== 8) {
    throw new ResponseDecodeError("$.version", "resource monitor wire version 8");
  }
  const capturedAt = requireNullable(
    snapshot.capturedAt,
    "$.capturedAt",
    requireTimestamp);
  const breakdown = decodeResourceBreakdownWireSnapshot(snapshot.breakdown);
  const table = decodeResourceTableWireSnapshot(snapshot.table, "$.table");
  return {
    capturedAt: capturedAt ?? undefined,
    breakdown,
    table
  };
}

function decodeSoftwareIdentity(value: unknown, path: string): SoftwareIdentityRow {
  const row = requireTuple(value, path, 4);
  return [
    requireNonEmptyString(row[0], `${path}[0]`),
    requireNonEmptyString(row[1], `${path}[1]`),
    requireNonEmptyString(row[2], `${path}[2]`),
    requireNonEmptyString(row[3], `${path}[3]`)
  ];
}

function decodeBar(
  value: unknown,
  softwareCatalog: SoftwareIdentityRow[],
  path: string
): ResourceBreakdownBar {
  const bar = requireRecord(value, path);
  const totalValue = requireNullable(
    bar.totalValue,
    `${path}.totalValue`,
    requireFiniteNumber);
  const capacityValue = requireNullable(
    bar.capacityValue,
    `${path}.capacityValue`,
    requireFiniteNumber);
  const totalSystemPercent = requireNullable(
    bar.totalSystemPercent,
    `${path}.totalSystemPercent`,
    requireFiniteNumber);
  if (totalValue === null || capacityValue === null || totalSystemPercent === null) {
    throw new ResponseDecodeError(path, "published bar with numeric totals");
  }

  return {
    metricId: requireNonEmptyString(bar.metricId, `${path}.metricId`),
    label: requireNonEmptyString(bar.label, `${path}.label`),
    unit: requireString(bar.unit, `${path}.unit`),
    scaleMode: requireOneOf(bar.scaleMode, `${path}.scaleMode`, scaleModes),
    totalValue,
    capacityValue,
    totalSystemPercent,
    totalDisplay: requireString(bar.totalDisplay, `${path}.totalDisplay`),
    software: requireArray(bar.software, `${path}.software`)
      .map((row, index) => decodeSoftware(
        row,
        softwareCatalog,
        `${path}.software[${index}]`))
  };
}

function decodeSoftware(
  value: unknown,
  softwareCatalog: SoftwareIdentityRow[],
  path: string
): ResourceSoftwareSegment {
  const row = requireTuple(value, path, 7);
  const softwareIndex = requireSafeInteger(row[0], `${path}[0]`);
  const identity = softwareCatalog[softwareIndex];
  if (!identity) {
    throw new ResponseDecodeError(`${path}[0]`, "software catalog index in range");
  }

  const [softwareId, name, kind, displayKind] = identity;
  return {
    softwareId,
    name,
    kind,
    displayKind,
    value: requireFiniteNumber(row[1], `${path}[1]`),
    systemPercent: requireFiniteNumber(row[2], `${path}[2]`),
    displayValue: requireString(row[3], `${path}[3]`),
    processCount: requireSafeInteger(row[4], `${path}[4]`),
    baseScore: requireFiniteNumber(row[5], `${path}[5]`),
    processes: requireArray(row[6], `${path}[6]`)
      .map((process, index) => decodeProcess(process, `${path}[6][${index}]`))
  };
}

function decodeProcess(value: unknown, path: string): ResourceProcessSegment {
  const row = requireTuple(value, path, 12);
  return {
    processId: requireSafeInteger(row[0], `${path}[0]`),
    processStartKey: requireNullable(
      row[11],
      `${path}[11]`,
      requirePositiveIntegerString),
    name: requireNonEmptyString(row[1], `${path}[1]`),
    executablePath: requireNullable(row[2], `${path}[2]`, requireString),
    value: requireFiniteNumber(row[3], `${path}[3]`),
    systemPercent: requireFiniteNumber(row[4], `${path}[4]`),
    softwarePercent: requireFiniteNumber(row[5], `${path}[5]`),
    displayValue: requireString(row[6], `${path}[6]`),
    userName: requireNullable(row[7], `${path}[7]`, requireString),
    architecture: requireNullable(row[8], `${path}[8]`, requireString),
    attributionKind: requireNonEmptyString(row[9], `${path}[9]`),
    baseScore: requireFiniteNumber(row[10], `${path}[10]`)
  };
}

export function decodeResourceTableWireSnapshot(
  value: unknown,
  path = "$"
): ResourceTableSnapshot {
  const table = requireRecord(value, path);
  const capturedAt = requireNullable(
    table.capturedAt,
    `${path}.capturedAt`,
    requireTimestamp);
  const sort = requireRecord(table.sort, `${path}.sort`);
  return {
    capturedAt: capturedAt ?? undefined,
    columns: requireArray(table.columns, `${path}.columns`)
      .map((column, index) => decodeTableColumn(column, `${path}.columns[${index}]`)),
    rows: requireArray(table.rows, `${path}.rows`)
      .map((row, index) => decodeTableRow(row, `${path}.rows[${index}]`)),
    sort: {
      columnId: requireNonEmptyString(sort.columnId, `${path}.sort.columnId`),
      direction: requireNonEmptyString(sort.direction, `${path}.sort.direction`)
    },
    viewMode: optionalString(table.viewMode, `${path}.viewMode`)
  };
}

function decodeTableColumn(value: unknown, path: string): ResourceTableColumn {
  const column = requireRecord(value, path);
  return {
    id: requireNonEmptyString(column.id, `${path}.id`),
    label: requireNonEmptyString(column.label, `${path}.label`),
    unit: requireString(column.unit, `${path}.unit`),
    visible: requireBoolean(column.visible, `${path}.visible`),
    sortable: requireBoolean(column.sortable, `${path}.sortable`),
    width: requireFiniteNumber(column.width, `${path}.width`)
  };
}

function decodeTableRow(value: unknown, path: string): ResourceTableRow {
  const row = requireRecord(value, path);
  const valuesRecord = requireRecord(row.values, `${path}.values`);
  const values: Record<string, ResourceTableValue> = {};
  for (const [key, cell] of Object.entries(valuesRecord)) {
    values[key] = decodeTableValue(cell, `${path}.values.${key}`);
  }

  return {
    id: requireNonEmptyString(row.id, `${path}.id`),
    parentId: optionalNullableString(row.parentId, `${path}.parentId`),
    depth: requireSafeInteger(row.depth, `${path}.depth`),
    kind: requireNonEmptyString(row.kind, `${path}.kind`),
    name: requireNonEmptyString(row.name, `${path}.name`),
    status: requireString(row.status, `${path}.status`),
    softwareName: optionalNullableString(row.softwareName, `${path}.softwareName`),
    softwareId: optionalNullableString(row.softwareId, `${path}.softwareId`),
    processId: optionalNullableNumber(row.processId, `${path}.processId`),
    processStartKey: optionalNullablePositiveIntegerString(
      row.processStartKey,
      `${path}.processStartKey`),
    processCount: requireSafeInteger(row.processCount, `${path}.processCount`),
    processIds: optionalNumberArray(row.processIds, `${path}.processIds`),
    processNames: optionalStringArray(row.processNames, `${path}.processNames`),
    executablePaths: optionalStringArray(row.executablePaths, `${path}.executablePaths`),
    impactScore: requireFiniteNumber(row.impactScore, `${path}.impactScore`),
    values,
    sortKeys: optionalNumberRecord(row.sortKeys, `${path}.sortKeys`)
  };
}

function decodeTableValue(value: unknown, path: string): ResourceTableValue {
  const cell = requireRecord(value, path);
  return {
    value: optionalNullableFiniteNumber(cell.value, `${path}.value`),
    percent: optionalNullableFiniteNumber(cell.percent, `${path}.percent`),
    displayValue: requireString(cell.displayValue, `${path}.displayValue`),
    unit: optionalString(cell.unit, `${path}.unit`),
    availability: optionalNullableString(cell.availability, `${path}.availability`),
    heatPercent: optionalNullableFiniteNumber(cell.heatPercent, `${path}.heatPercent`),
    sharedValue: optionalNullableFiniteNumber(cell.sharedValue, `${path}.sharedValue`),
    attributionKind: optionalString(cell.attributionKind, `${path}.attributionKind`)
  };
}

function requireTuple(value: unknown, path: string, length: number): unknown[] {
  const row = requireArray(value, path);
  if (row.length !== length) {
    throw new ResponseDecodeError(path, `${length}-item tuple`);
  }
  return row;
}

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "valid timestamp string");
  }
  return timestamp;
}

function requirePositiveIntegerString(value: unknown, path: string): string {
  const text = requireString(value, path);
  if (!/^[1-9][0-9]*$/.test(text)) {
    throw new ResponseDecodeError(path, "canonical positive integer string");
  }
  return text;
}

function optionalString(value: unknown, path: string): string | undefined {
  return value === undefined ? undefined : requireString(value, path);
}

function optionalNullableString(
  value: unknown,
  path: string
): string | null | undefined {
  return value === undefined ? undefined : requireNullable(value, path, requireString);
}

function optionalNullableNumber(
  value: unknown,
  path: string
): number | null | undefined {
  return value === undefined ? undefined : requireNullable(value, path, requireSafeInteger);
}

function optionalNullablePositiveIntegerString(
  value: unknown,
  path: string
): string | null | undefined {
  return value === undefined
    ? undefined
    : requireNullable(value, path, requirePositiveIntegerString);
}

function optionalNullableFiniteNumber(
  value: unknown,
  path: string
): number | null | undefined {
  return value === undefined ? undefined : requireNullable(value, path, requireFiniteNumber);
}

function optionalStringArray(value: unknown, path: string): string[] | undefined {
  return value === undefined
    ? undefined
    : requireArray(value, path).map(
      (item, index) => requireString(item, `${path}[${index}]`));
}

function optionalNumberArray(value: unknown, path: string): number[] | undefined {
  return value === undefined
    ? undefined
    : requireArray(value, path).map(
      (item, index) => requireSafeInteger(item, `${path}[${index}]`));
}

function optionalNumberRecord(
  value: unknown,
  path: string
): Record<string, number> | undefined {
  if (value === undefined) {
    return undefined;
  }
  const record = requireRecord(value, path);
  return Object.fromEntries(Object.entries(record).map(([key, item]) => [
    key,
    requireFiniteNumber(item, `${path}.${key}`)
  ]));
}
