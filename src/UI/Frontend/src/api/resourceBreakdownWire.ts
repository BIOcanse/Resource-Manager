import type {
  ResourceBreakdownBar,
  ResourceBreakdownSnapshot,
  ResourceMonitorSnapshot,
  ResourceProcessDetailsSnapshot,
  ResourceProcessSegment,
  ResourceScaleMode,
  ResourceSoftwareSegment
} from "../types";
import { decodeResourceFacts, projectResourceFacts } from "../features/resourceTable/resourceFacts.ts";
import {
  requireArray,
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
  userName: string | null,
  architecture: string | null,
  attributionKind: string,
  baseScore: number,
  processStartKey: string | null,
  sharedValue?: number
];

type SoftwareValueRow = [
  softwareIndex: number,
  value: number,
  systemPercent: number,
  processCount: number,
  baseScore: number,
  processes: ProcessRow[],
  sharedValue?: number
];

interface ResourceBreakdownBarWire {
  metricId: string;
  unit: string;
  scaleMode: ResourceScaleMode;
  totalValue: number | null;
  capacityValue: number | null;
  totalSystemPercent: number | null;
  sharedValue?: number | null;
  software: SoftwareValueRow[];
}

export interface ResourceBreakdownWireSnapshot {
  version: 8;
  capturedAt: string | null;
  softwareCatalog: SoftwareIdentityRow[];
  bars: ResourceBreakdownBarWire[];
}

export interface ResourceMonitorWireSnapshot {
  version: 10;
  capturedAt: string | null;
  breakdown: ResourceBreakdownWireSnapshot;
}


const scaleModes = ["capacity", "active"] as const;

export function decodeResourceBreakdownWireSnapshot(
  value: unknown
): ResourceBreakdownSnapshot {
  const snapshot = requireRecord(value);
  if (requireSafeInteger(snapshot.version, "$.version") !== 8) {
    throw new ResponseDecodeError("$.version", "resource breakdown wire version 8");
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
  if (snapshot.version === 2) return decodeResourceFacts(value);
  if (requireSafeInteger(snapshot.version, "$.version") !== 10) {
    throw new ResponseDecodeError("$.version", "resource monitor wire version 10");
  }
  const capturedAt = requireNullable(
    snapshot.capturedAt,
    "$.capturedAt",
    requireTimestamp);
  const breakdown = decodeResourceBreakdownWireSnapshot(snapshot.breakdown);
  const table = projectResourceFacts({ capturedAt: capturedAt ?? undefined, metrics: [] }, [], "software", "impact", "desc");
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
    // 只代表一个分组的行（Windows 系统、Windows 服务、未归属进程…）没有软件名，
    // 名字由前端按分组标识出，所以这里允许空串。
    requireString(row[1], `${path}[1]`),
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

  // 后端只在进程内存类的条上写这两项（自动的整机内存条写 processFieldName，按字段订阅的条两项都写）；
  // 写了就必须是非空字符串。
  const optionalName = (key: "processFieldName" | "totalMetricId") =>
    bar[key] === undefined ? null : requireNonEmptyString(bar[key], `${path}.${key}`);
  return {
    metricId: requireNonEmptyString(bar.metricId, `${path}.metricId`),
    processFieldName: optionalName("processFieldName"),
    totalMetricId: optionalName("totalMetricId"),
    unit: requireString(bar.unit, `${path}.unit`),
    scaleMode: requireOneOf(bar.scaleMode, `${path}.scaleMode`, scaleModes),
    totalValue,
    capacityValue,
    totalSystemPercent,
    sharedValue: optionalNullableFiniteNumber(bar.sharedValue, `${path}.sharedValue`),
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
  const row = requireTuple(value, path, 6, 7);
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
    processCount: requireSafeInteger(row[3], `${path}[3]`),
    baseScore: requireFiniteNumber(row[4], `${path}[4]`),
    sharedValue: optionalNullableFiniteNumber(row[6], `${path}[6]`),
    processes: requireArray(row[5], `${path}[5]`)
      .map((process, index) => decodeProcess(process, `${path}[5][${index}]`))
  };
}

export function decodeResourceProcessDetailsWireSnapshot(value: unknown): ResourceProcessDetailsSnapshot {
  const snapshot = requireRecord(value);
  if (requireSafeInteger(snapshot.version, "$.version") !== 1) {
    throw new ResponseDecodeError("$.version", "resource process details wire version 1");
  }
  return {
    capturedAt: requireNullable(snapshot.capturedAt, "$.capturedAt", requireTimestamp) ?? undefined,
    metrics: requireArray(snapshot.metrics, "$.metrics").map((value, index) => {
      const path = `$.metrics[${index}]`;
      const metric = requireRecord(value, path);
      return {
        metricId: requireNonEmptyString(metric.metricId, `${path}.metricId`),
        software: requireArray(metric.software, `${path}.software`).map((value, index) => {
          const softwarePath = `${path}.software[${index}]`;
          const software = requireRecord(value, softwarePath);
          return {
            softwareId: requireNonEmptyString(software.softwareId, `${softwarePath}.softwareId`),
            processes: requireArray(software.processes, `${softwarePath}.processes`)
              .map((value, index) => decodeProcess(value, `${softwarePath}.processes[${index}]`))
          };
        })
      };
    })
  };
}

function decodeProcess(value: unknown, path: string): ResourceProcessSegment {
  const row = requireTuple(value, path, 11, 12);
  return {
    processId: requireSafeInteger(row[0], `${path}[0]`),
    sharedValue: optionalNullableFiniteNumber(row[11], `${path}[11]`),
    processStartKey: requireNullable(
      row[10],
      `${path}[10]`,
      requirePositiveIntegerString),
    name: requireString(row[1], `${path}[1]`),
    executablePath: requireNullable(row[2], `${path}[2]`, requireString),
    value: requireFiniteNumber(row[3], `${path}[3]`),
    systemPercent: requireFiniteNumber(row[4], `${path}[4]`),
    softwarePercent: requireFiniteNumber(row[5], `${path}[5]`),
    userName: requireNullable(row[6], `${path}[6]`, requireString),
    architecture: requireNullable(row[7], `${path}[7]`, requireString),
    attributionKind: requireNonEmptyString(row[8], `${path}[8]`),
    baseScore: requireFiniteNumber(row[9], `${path}[9]`)
  };
}

function requireTuple(value: unknown, path: string, length: number, optionalLength = length): unknown[] {
  const row = requireArray(value, path);
  if (row.length !== length && row.length !== optionalLength) {
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

function optionalNullableFiniteNumber(
  value: unknown,
  path: string
): number | null | undefined {
  return value === undefined ? undefined : requireNullable(value, path, requireFiniteNumber);
}
