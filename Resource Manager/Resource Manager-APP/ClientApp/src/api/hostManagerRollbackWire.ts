import type {
  HostManagerAppliedPlacementReceipt,
  HostManagerAppliedRecord,
  HostManagerRollbackStateDocument
} from "../types";

export function decodeHostManagerRollbackState(
  value: unknown): HostManagerRollbackStateDocument
{
  const document = requireObject(value, "Host Manager state");
  const version = requireInteger(document.version, "version");
  if (version !== 2) {
    throw new Error(`Unsupported Host Manager state version: ${version}`);
  }

  return {
    version,
    lastRunAt: optionalNullableString(document.lastRunAt, "lastRunAt"),
    lastRestoreAt: optionalNullableString(document.lastRestoreAt, "lastRestoreAt"),
    message: requireString(document.message, "message"),
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
    displayName: requireString(placement.displayName, "displayName"),
    softwareId: optionalNullableString(placement.softwareId, "softwareId"),
    resourceKind: requireString(placement.resourceKind, "resourceKind"),
    records: requireArray(placement.records, "records").map(decodeRecord),
    appliedAt: requireString(placement.appliedAt, "appliedAt"),
    updatedAt: requireString(placement.updatedAt, "updatedAt")
  };
}

function decodeRecord(value: unknown, index: number): HostManagerAppliedRecord {
  const record = requireObject(value, `records[${index}]`);
  return {
    kind: requireString(record.kind, "kind"),
    recordId: requireString(record.recordId, "recordId"),
    metadata: decodeMetadata(record.metadata)
  };
}

function decodeMetadata(value: unknown): Record<string, string> | null | undefined {
  if (value === undefined || value === null) {
    return value;
  }
  const metadata = requireObject(value, "metadata");
  return Object.fromEntries(
    Object.entries(metadata).map(([key, entry]) => [
      key,
      requireString(entry, `metadata.${key}`)
    ]));
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
