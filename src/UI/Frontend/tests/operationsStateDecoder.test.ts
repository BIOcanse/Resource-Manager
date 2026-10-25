import { sameOperationSnapshot } from "../src/frontendRuntime/operations/operationMerge.ts";
import assert from "node:assert/strict";
import {
  operationSnapshotDecoder,
  operationsStateDecoder
} from "../src/data/operations/operationsStateDecoder.ts";
import {
  compareUInt64Decimal,
  formatUInt64Bytes,
  isCanonicalUInt64Decimal
} from "../src/data/operations/uint64Decimal.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const operation = {
  id: "00000000000000000000000000000001",
  kind: "component.install",
  domainKey: "component.example",
  subject: { targetId: "example", scope: null, mode: null, targetPath: null },
  state: "running",
  configurationGeneration: "18446744073709551615",
  stateRevision: "42",
  attemptNumber: 1,
  maximumAttempts: 3,
  createdAt: "2026-08-22T12:00:00.000Z",
  updatedAt: "2026-08-22T12:00:01.000+00:00",
  completedAt: null,
  cancelRequested: false,
  progress: {
    sequence: "9",
    percent: 25.5,
    bytesDone: "1024",
    bytesTotal: "4096",
    speedBytesPerSecond: "18446744073709551615",
    stage: "Download",
  }
};

const envelope = {
  schema: "host-manager.operations.state.v2",
  capturedAt: "2026-08-22T12:00:02.000Z",
  publicationRevision: "18446744073709551615",
  configurationGeneration: "7",
  ready: true,
  persistenceFaulted: false,
  faultStage: null,
  operations: [operation]
};

const decoded = operationsStateDecoder.decode(envelope);
assert.equal(decoded.publicationRevision, envelope.publicationRevision);
assert.equal(
  decoded.operations[0].progress?.speedBytesPerSecond,
  "18446744073709551615");
assert.notEqual(decoded, envelope);
assert.notEqual(decoded.operations[0], operation);
assert.equal(operationSnapshotDecoder.decode(operation).id, operation.id);
const withUnrecognizedDiagnostics = operationsStateDecoder.decode({ ...envelope, faultMessage: "internal fault",
  operations: [{ ...operation, diagnostic: "internal exception" }] });
assert.equal("faultMessage" in withUnrecognizedDiagnostics, false);
assert.equal("diagnostic" in withUnrecognizedDiagnostics.operations[0], false);

assert.equal(isCanonicalUInt64Decimal("0"), true);
assert.equal(isCanonicalUInt64Decimal("18446744073709551615"), true);
assert.equal(isCanonicalUInt64Decimal("18446744073709551616"), false);
assert.equal(isCanonicalUInt64Decimal("01"), false);
assert.equal(compareUInt64Decimal("10", "9"), 1);
// 下载体积是传输量，按十进制显示：1024 字节就是 1.02 kB。
// 进制与标签由 presentation/byteUnits.ts 统一决定，见 byteUnitsContract。
assert.equal(formatUInt64Bytes("1024"), "1.02 kB");
assert.equal(formatUInt64Bytes("1000"), "1.00 kB");

const invalidCases: Array<[unknown, string]> = [
  [[], "$"],
  [{ ...envelope, schema: "host-manager.operations.state.v0" }, "$.schema"],
  [{ ...envelope, publicationRevision: 1 }, "$.publicationRevision"],
  [{ ...envelope, publicationRevision: "01" }, "$.publicationRevision"],
  [{ ...envelope, publicationRevision: "18446744073709551616" }, "$.publicationRevision"],
  [{ ...envelope, capturedAt: "2026-08-22T12:00:02.000-05:00" }, "$.capturedAt"],
  [{ ...envelope, operations: [operation, { ...operation }] }, "$.operations[1].id"],
  [{ ...envelope, operations: [{ ...operation, id: "0".repeat(32) }] }, "$.operations[0].id"],
  [{ ...envelope, operations: [{ ...operation, id: "A".repeat(32) }] }, "$.operations[0].id"],
  [{ ...envelope, operations: [{ ...operation, stateRevision: 42 }] }, "$.operations[0].stateRevision"],
  [{ ...envelope, operations: [{ ...operation, attemptNumber: 4 }] }, "$.operations[0].attemptNumber"],
  [{ ...envelope, operations: [{ ...operation, subject: { ...operation.subject, targetId: 123 } }] }, "$.operations[0].subject.targetId"],
  [{
    ...envelope,
    operations: [{
      ...operation,
      progress: { ...operation.progress, bytesDone: "4097" }
    }]
  }, "$.operations[0].progress.bytesDone"],
  [{
    ...envelope,
    operations: [{ ...operation, updatedAt: "2026-08-22T12:00:03.000Z" }]
  }, "$.operations[0].updatedAt"],
  [{
    ...envelope,
    operations: [{
      ...operation,
      state: "succeeded",
      completedAt: null
    }]
  }, "$.operations[0].completedAt"]
];

for (const [value, expectedPath] of invalidCases) {
  assert.throws(
    () => operationsStateDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}

assert.throws(
  () => operationSnapshotDecoder.decode([]),
  (error: unknown) => error instanceof ResponseDecodeError
    && error.path === "$");

const withFeedback = operationSnapshotDecoder.decode({ ...operation,
  resultCode: "InstallerLaunched", resultMessage: { domain: 1, code: 22, args: [] },
  errorMessage: { domain: 6, code: 1, args: [] }
});
assert.equal(withFeedback.resultCode, "InstallerLaunched");
assert.deepEqual(withFeedback.resultMessage, { domain: 1, code: 22, args: [] });
assert.deepEqual(withFeedback.errorMessage, { domain: 6, code: 1, args: [] });
assert.throws(() => operationSnapshotDecoder.decode({ ...operation, resultMessage: { domain: 256, code: 1, args: [] } }), ResponseDecodeError);
assert.throws(() => operationSnapshotDecoder.decode({ ...operation, errorMessage: { domain: 6, code: 1, args: [4] } }), ResponseDecodeError);

assert.equal(sameOperationSnapshot(withFeedback, { ...withFeedback, resultMessage: { domain: 1, code: 22, args: [] } }), true);
assert.equal(sameOperationSnapshot(withFeedback, { ...withFeedback, resultMessage: { domain: 1, code: 21, args: [] } }), false);
assert.equal(sameOperationSnapshot(withFeedback, { ...withFeedback, errorMessage: { domain: 6, code: 2, args: [] } }), false);
assert.equal(sameOperationSnapshot(withFeedback, { ...withFeedback, resultCode: "Installed" }), false);
