import type { OperationProgress, OperationSnapshot } from "../../types.ts";
import {
  compareUInt64Decimal
} from "../../data/operations/uint64Decimal.ts";
import type {
  HostManagerOperationsState
} from "../../data/operations/operationsStateDecoder.ts";

export function isTerminalOperation(operation: OperationSnapshot): boolean {
  return operation.state === "succeeded"
    || operation.state === "failed"
    || operation.state === "canceled"
    || operation.state === "stateUncertain";
}

export function compareOperationVersion(
  left: OperationSnapshot,
  right: OperationSnapshot
): number {
  const configuration = compareUInt64Decimal(
    left.configurationGeneration,
    right.configurationGeneration);
  if (configuration !== 0) {
    return configuration;
  }
  const state = compareUInt64Decimal(left.stateRevision, right.stateRevision);
  if (state !== 0) {
    return state;
  }
  const progress = compareOptionalRevision(
    left.progress?.sequence ?? null,
    right.progress?.sequence ?? null);
  if (progress !== 0) {
    return progress;
  }
  return 0;
}

export function sameOperationSnapshot(
  left: OperationSnapshot,
  right: OperationSnapshot
): boolean {
  return left.id === right.id
    && left.kind === right.kind
    && left.domainKey === right.domainKey
    && left.title === right.title
    && left.state === right.state
    && left.configurationGeneration === right.configurationGeneration
    && left.stateRevision === right.stateRevision
    && left.attemptNumber === right.attemptNumber
    && left.maximumAttempts === right.maximumAttempts
    && left.createdAt === right.createdAt
    && left.updatedAt === right.updatedAt
    && left.completedAt === right.completedAt
    && left.cancelRequested === right.cancelRequested
    && sameProgress(left.progress, right.progress)
    && left.result === right.result
    && left.error === right.error;
}

export function sameOperationsState(
  left: HostManagerOperationsState,
  right: HostManagerOperationsState
): boolean {
  if (left.schema !== right.schema
    || left.capturedAt !== right.capturedAt
    || left.publicationRevision !== right.publicationRevision
    || left.configurationGeneration !== right.configurationGeneration
    || left.ready !== right.ready
    || left.persistenceFaulted !== right.persistenceFaulted
    || left.faultStage !== right.faultStage
    || left.faultMessage !== right.faultMessage
    || left.operations.length !== right.operations.length) {
    return false;
  }
  return left.operations.every((operation, index) =>
    sameOperationSnapshot(operation, right.operations[index]));
}

function compareOptionalRevision(
  left: string | null,
  right: string | null
): number {
  if (left === null || right === null) {
    return left === right ? 0 : left === null ? -1 : 1;
  }
  return compareUInt64Decimal(left, right);
}

function sameProgress(
  left: OperationProgress | null,
  right: OperationProgress | null
): boolean {
  if (left === null || right === null) {
    return left === right;
  }
  return left.sequence === right.sequence
    && left.percent === right.percent
    && left.bytesDone === right.bytesDone
    && left.bytesTotal === right.bytesTotal
    && left.speedBytesPerSecond === right.speedBytesPerSecond
    && left.stage === right.stage
    && left.message === right.message;
}
