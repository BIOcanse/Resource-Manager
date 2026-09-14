import {
  defineResponseDecoder,
  requireArray,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  requireStringArray,
  ResponseDecodeError
} from "../frontendRuntime/request/ResponseDecoder.ts";
import type {
  DeviceTopologySnapshot,
  DeviceTopologySnapshotSource,
  DeviceTopologySnapshotState,
  DeviceTopologySnapshotStatus,
  DeviceTopologySourceDiagnostic,
  DeviceTopologySourceDiagnosticStatus
} from "../types.ts";
import { decodePort, decodeSystemIdentity } from "./deviceTopologyNestedDecoder.ts";

const currentSchemaVersion = "3.0.0";
const statuses = ["warming", "ready", "refreshing", "failed"] as const;
const sources = ["memory", "persisted", "live"] as const;
const diagnosticStatuses = ["required-incomplete", "optional-degraded"] as const;

export const deviceTopologyStateDecoder =
  defineResponseDecoder<DeviceTopologySnapshotState>(
    "device-topology.state.v3",
    (value) => {
      const record = requireRecord(value);
      const schemaVersion = requireString(record.schemaVersion, "$.schemaVersion");
      if (schemaVersion !== currentSchemaVersion) {
        throw new ResponseDecodeError(
          "$.schemaVersion",
          `schema version '${currentSchemaVersion}'`);
      }
      const state = requireOneOf(
        record.state,
        "$.state",
        statuses) as DeviceTopologySnapshotStatus;
      const snapshot = requireNullable(
        record.snapshot,
        "$.snapshot",
        decodeSnapshot);
      if (state === "ready" && snapshot === null) {
        throw new ResponseDecodeError("$.snapshot", "snapshot when state is 'ready'");
      }

      return {
        schemaVersion,
        state,
        snapshot,
        contentGeneration: requireNonNegativeSafeInteger(
          record.contentGeneration,
          "$.contentGeneration"),
        stateRevision: requireNonNegativeSafeInteger(
          record.stateRevision,
          "$.stateRevision"),
        source: requireOneOf(
          record.source,
          "$.source",
          sources) as DeviceTopologySnapshotSource,
        lastSuccessAt: nullableTimestamp(record.lastSuccessAt, "$.lastSuccessAt"),
        lastAttemptAt: nullableTimestamp(record.lastAttemptAt, "$.lastAttemptAt"),
        failureCode: requireNullable(record.failureCode, "$.failureCode", requireNonEmptyString),
        attemptDiagnostics: requireArray(
          record.attemptDiagnostics,
          "$.attemptDiagnostics")
          .map((item, index) => decodeSourceDiagnostic(
            item,
            `$.attemptDiagnostics[${index}]`))
      };
    });

function decodeSourceDiagnostic(
  value: unknown,
  path: string
): DeviceTopologySourceDiagnostic {
  const record = requireRecord(value, path);
  return {
    sourceId: requireNonEmptyString(record.sourceId, `${path}.sourceId`),
    status: requireOneOf(
      record.status,
      `${path}.status`,
      diagnosticStatuses) as DeviceTopologySourceDiagnosticStatus,
    code: requireNonEmptyString(record.code, `${path}.code`),
    message: requireNonEmptyString(record.message, `${path}.message`)
  };
}

function decodeSnapshot(value: unknown, path: string): DeviceTopologySnapshot {
  const record = requireRecord(value, path);
  const ports = requireArray(record.ports, `${path}.ports`)
    .map((item, index) => decodePort(item, `${path}.ports[${index}]`));
  const portIds = new Set<string>();
  for (let index = 0; index < ports.length; index += 1) {
    const id = ports[index].id;
    if (portIds.has(id)) {
      throw new ResponseDecodeError(`${path}.ports[${index}].id`, "unique port id");
    }
    portIds.add(id);
  }
  return {
    capturedAt: requireTimestamp(record.capturedAt, `${path}.capturedAt`),
    system: decodeSystemIdentity(record.system, `${path}.system`),
    ports,
    notes: requireStringArray(record.notes, `${path}.notes`)
  };
}

function nullableTimestamp(value: unknown, path: string): string | null {
  return requireNullable(value, path, requireTimestamp);
}

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireString(value, path);
  if (!timestamp || !Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "valid timestamp string");
  }
  return timestamp;
}
