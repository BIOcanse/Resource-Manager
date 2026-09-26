import type {
  OperationProgress,
  OperationSnapshot,
  OperationState
} from "../../types.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import {
  compareUInt64Decimal,
  isCanonicalUInt64Decimal
} from "./uint64Decimal.ts";

export const operationsStateSchema = "host-manager.operations.state.v1" as const;

export interface HostManagerOperationsState {
  readonly schema: typeof operationsStateSchema;
  readonly capturedAt: string;
  readonly publicationRevision: string;
  readonly configurationGeneration: string;
  readonly ready: boolean;
  readonly persistenceFaulted: boolean;
  readonly faultStage: string | null;
  readonly faultMessage: string | null;
  readonly operations: readonly OperationSnapshot[];
}

const operationStates = [
  "queued",
  "startPending",
  "running",
  "cancelPending",
  "retryWait",
  "recoveryPending",
  "succeeded",
  "failed",
  "canceled",
  "stateUncertain"
] as const satisfies readonly OperationState[];

const terminalStates = new Set<OperationState>([
  "succeeded",
  "failed",
  "canceled",
  "stateUncertain"
]);

export const operationSnapshotDecoder = defineResponseDecoder<OperationSnapshot>(
  "host-manager.operation.v1",
  (value) => decodeOperationSnapshot(value, "$"));

export const operationsStateDecoder = defineResponseDecoder<HostManagerOperationsState>(
  operationsStateSchema,
  (value) => {
    const record = requireRecord(value);
    const schema = requireString(record.schema, "$.schema");
    if (schema !== operationsStateSchema) {
      throw new ResponseDecodeError("$.schema", `'${operationsStateSchema}'`);
    }
    const capturedAt = requireUtcTimestamp(record.capturedAt, "$.capturedAt");
    const publicationRevision = requireUInt64Decimal(
      record.publicationRevision,
      "$.publicationRevision");
    const configurationGeneration = requireUInt64Decimal(
      record.configurationGeneration,
      "$.configurationGeneration");
    const operations = requireArray(record.operations, "$.operations")
      .map((operation, index) => decodeOperationSnapshot(
        operation,
        `$.operations[${index}]`));
    const ids = new Set<string>();
    for (let index = 0; index < operations.length; index += 1) {
      const operation = operations[index];
      if (ids.has(operation.id)) {
        throw new ResponseDecodeError(
          `$.operations[${index}].id`,
          "unique operation id");
      }
      ids.add(operation.id);
      if (Date.parse(operation.updatedAt) > Date.parse(capturedAt)) {
        throw new ResponseDecodeError(
          `$.operations[${index}].updatedAt`,
          "timestamp not later than $.capturedAt");
      }
    }

    return Object.freeze({
      schema: operationsStateSchema,
      capturedAt,
      publicationRevision,
      configurationGeneration,
      ready: requireBoolean(record.ready, "$.ready"),
      persistenceFaulted: requireBoolean(
        record.persistenceFaulted,
        "$.persistenceFaulted"),
      faultStage: requireNullable(record.faultStage, "$.faultStage", requireString),
      faultMessage: requireNullable(
        record.faultMessage,
        "$.faultMessage",
        requireString),
      operations: Object.freeze(operations)
    });
  });

function decodeOperationSnapshot(value: unknown, path: string): OperationSnapshot {
  const record = requireRecord(value, path);
  const id = requireNonEmptyString(record.id, `${path}.id`);
  if (!/^[0-9a-f]{32}$/.test(id) || /^0{32}$/.test(id)) {
    throw new ResponseDecodeError(`${path}.id`, "non-zero 32-character lowercase hex id");
  }
  const state = requireOneOf(record.state, `${path}.state`, operationStates);
  const attemptNumber = requireNonNegativeSafeInteger(
    record.attemptNumber,
    `${path}.attemptNumber`);
  const maximumAttempts = requireNonNegativeSafeInteger(
    record.maximumAttempts,
    `${path}.maximumAttempts`);
  if (maximumAttempts === 0 || attemptNumber > maximumAttempts) {
    throw new ResponseDecodeError(
      `${path}.attemptNumber`,
      "integer not greater than maximumAttempts");
  }
  const createdAt = requireUtcTimestamp(record.createdAt, `${path}.createdAt`);
  const updatedAt = requireUtcTimestamp(record.updatedAt, `${path}.updatedAt`);
  if (Date.parse(createdAt) > Date.parse(updatedAt)) {
    throw new ResponseDecodeError(
      `${path}.updatedAt`,
      "timestamp not earlier than createdAt");
  }
  const completedAt = requireNullable(
    record.completedAt,
    `${path}.completedAt`,
    requireUtcTimestamp);
  if (terminalStates.has(state) !== (completedAt !== null)) {
    throw new ResponseDecodeError(
      `${path}.completedAt`,
      terminalStates.has(state)
        ? "UTC timestamp for a terminal operation"
        : "null for a non-terminal operation");
  }
  if (completedAt !== null && Date.parse(completedAt) < Date.parse(updatedAt)) {
    throw new ResponseDecodeError(
      `${path}.completedAt`,
      "timestamp not earlier than updatedAt");
  }

  return Object.freeze({
    id,
    kind: requireNonEmptyString(record.kind, `${path}.kind`),
    domainKey: requireNullable(record.domainKey, `${path}.domainKey`, requireString),
    title: requireNullable(record.title, `${path}.title`, requireString),
    state,
    configurationGeneration: requireUInt64Decimal(
      record.configurationGeneration,
      `${path}.configurationGeneration`),
    stateRevision: requireUInt64Decimal(
      record.stateRevision,
      `${path}.stateRevision`),
    attemptNumber,
    maximumAttempts,
    createdAt,
    updatedAt,
    completedAt,
    cancelRequested: requireBoolean(
      record.cancelRequested,
      `${path}.cancelRequested`),
    progress: requireNullable(
      record.progress,
      `${path}.progress`,
      decodeProgress),
    result: requireNullable(record.result, `${path}.result`, requireString),
    error: requireNullable(record.error, `${path}.error`, requireString)
  });
}

function decodeProgress(value: unknown, path: string): OperationProgress {
  const record = requireRecord(value, path);
  const percent = requireNullable(
    record.percent,
    `${path}.percent`,
    requireFiniteNumber);
  if (percent !== null && (percent < 0 || percent > 100)) {
    throw new ResponseDecodeError(`${path}.percent`, "number from 0 through 100");
  }
  const bytesDone = requireNullable(
    record.bytesDone,
    `${path}.bytesDone`,
    requireUInt64Decimal);
  const bytesTotal = requireNullable(
    record.bytesTotal,
    `${path}.bytesTotal`,
    requireUInt64Decimal);
  if (bytesDone !== null
    && bytesTotal !== null
    && compareUInt64Decimal(bytesDone, bytesTotal) > 0) {
    throw new ResponseDecodeError(
      `${path}.bytesDone`,
      "uint64 decimal not greater than bytesTotal");
  }

  return Object.freeze({
    sequence: requireUInt64Decimal(record.sequence, `${path}.sequence`),
    percent,
    bytesDone,
    bytesTotal,
    speedBytesPerSecond: requireNullable(
      record.speedBytesPerSecond,
      `${path}.speedBytesPerSecond`,
      requireUInt64Decimal),
    stage: requireNullable(record.stage, `${path}.stage`, requireString),
    message: requireNullable(record.message, `${path}.message`, requireString)
  });
}

function requireUInt64Decimal(value: unknown, path: string): string {
  if (!isCanonicalUInt64Decimal(value)) {
    throw new ResponseDecodeError(path, "canonical uint64 decimal string");
  }
  return value;
}

function requireUtcTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(timestamp))
    || !/(?:Z|[+-]00:00)$/i.test(timestamp)) {
    throw new ResponseDecodeError(path, "valid UTC timestamp string");
  }
  return timestamp;
}
