import type {
  HostManagerCoordinatorFacts,
  HostManagerAppliedPlacementReceipt,
  HostManagerAppliedRecord,
  HostManagerRollbackStateDocument
} from "../types";

export function decodeHostManagerRollbackState(
  value: unknown): HostManagerRollbackStateDocument
{
  const document = requireObject(value, "Host Manager state");
  const version = requireInteger(document.version, "version");
  if (version !== 3) {
    throw new Error(`Unsupported Host Manager state version: ${version}`);
  }

  return {
    version,
    lastRunAt: optionalNullableString(document.lastRunAt, "lastRunAt"),
    lastRestoreAt: optionalNullableString(document.lastRestoreAt, "lastRestoreAt"),
    coordinator: decodeCoordinatorFacts(document.coordinator),
    appliedPlacements: requireArray(
      document.appliedPlacements,
      "appliedPlacements").map(decodePlacement)
  };
}

function decodePlacement(
  value: unknown,
  index: number): HostManagerAppliedPlacementReceipt
{
  const placement = requireObject(value, `appliedPlacements[${index}]`);
  return {
    targetId: requireString(placement.targetId, "targetId"),
    processId: placement.processId === null ? null : requireInteger(placement.processId, "processId"),
    processStartKey: optionalNullableString(placement.processStartKey, "processStartKey") ?? null,
    processName: optionalNullableString(placement.processName, "processName") ?? null,
    softwareId: optionalNullableString(placement.softwareId, "softwareId"),
    resourceKind: requireString(placement.resourceKind, "resourceKind"),
    records: requireArray(placement.records, "records").map(decodeRecord),
    appliedAt: requireString(placement.appliedAt, "appliedAt"),
    updatedAt: requireString(placement.updatedAt, "updatedAt")
  };
}

function decodeRecord(value: unknown, index: number): HostManagerAppliedRecord {
  const record = requireObject(value, `records[${index}]`);
  // 序号、状态、记录时间同有同无：记录在历史表里有行才有（docs/report_lifecycle_2026-10-28.md）。
  const identified = record.sequence !== null;
  const sequence = identified ? requireInteger(record.sequence, "sequence") : null;
  if (sequence !== null && sequence < 1) {
    throw new Error("Invalid sequence");
  }
  if (identified ? record.state !== "active" : record.state !== null) {
    throw new Error("Invalid state");
  }
  if (identified ? typeof record.recordedAt !== "string" : record.recordedAt !== null) {
    throw new Error("Invalid recordedAt");
  }
  return {
    kind: requireString(record.kind, "kind"),
    recordId: requireString(record.recordId, "recordId"),
    sequence,
    state: identified ? "active" : null,
    recordedAt: identified ? record.recordedAt as string : null
  };
}

function requireObject(
  value: unknown,
  field: string): Record<string, unknown>
{
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`Invalid ${field}`);
  }
  return value as Record<string, unknown>;
}

function requireArray(value: unknown, field: string): unknown[] {
  if (!Array.isArray(value)) {
    throw new Error(`Invalid ${field}`);
  }
  return value;
}

function requireString(value: unknown, field: string): string {
  if (typeof value !== "string") {
    throw new Error(`Invalid ${field}`);
  }
  return value;
}

function requireInteger(value: unknown, field: string): number {
  if (!Number.isInteger(value)) {
    throw new Error(`Invalid ${field}`);
  }
  return value as number;
}

function optionalNullableString(
  value: unknown,
  field: string): string | null | undefined
{
  if (value === undefined || value === null) {
    return value;
  }
  return requireString(value, field);
}

export function decodeCoordinatorFacts(value: unknown): HostManagerCoordinatorFacts | null {
  if (value === null) return null;
  const facts = requireObject(value, "coordinator");
  const exact = (value: unknown, field: string) => {
    const text = requireString(value, field);
    if (!/^(0|[1-9][0-9]*)$/.test(text) || BigInt(text) > 18446744073709551615n)
      throw new Error(`Invalid exact unsigned integer: ${field}`);
    return text;
  };
  const count = (value: unknown, field: string) => {
    const number = requireInteger(value, field);
    if (number < 0 || number > 4294967295) throw new Error(`Invalid count: ${field}`);
    return number;
  };
  return {
    revision: exact(facts.revision, "coordinator.revision"),
    pendingCount: count(facts.pendingCount, "coordinator.pendingCount"),
    inflightCount: count(facts.inflightCount, "coordinator.inflightCount"),
    reasonMask: exact(facts.reasonMask, "coordinator.reasonMask")
  };
}
