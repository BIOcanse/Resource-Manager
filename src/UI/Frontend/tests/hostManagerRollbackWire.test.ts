import assert from "node:assert/strict";
import { decodeHostManagerRollbackState } from "../src/api/hostManagerRollbackWire.ts";

const decoded = decodeHostManagerRollbackState({
  version: 3,
  lastRunAt: null,
  lastRestoreAt: "2026-07-24T12:00:00Z",
  coordinator: null,
  nativeHostSessionIncarnation: 7,
  appliedPlacements: [
    {
      targetId: "target",
      processId: 42, processStartKey: "123", processName: "example",
      softwareId: null,
      resourceKind: "gpu",
      appliedAt: "2026-07-24T12:00:00Z",
      updatedAt: "2026-07-24T12:01:00Z",
      records: [
        {
          kind: "gpu_preference",
          recordId: "record",
          sequence: null,
          state: null,
          recordedAt: null,
          metadata: { result: "ok" }
        },
        {
          kind: "cpu_affinity",
          recordId: "kept",
          sequence: 42,
          state: "active",
          recordedAt: "2026-07-24T12:00:01Z"
        }
      ]
    }
  ]
});

assert.equal(decoded.version, 3);
assert.equal(decoded.appliedPlacements.length, 1);
assert.equal("metadata" in decoded.appliedPlacements[0]!.records[0]!, false);
assert.equal("pendingChanges" in decoded, false);
assert.equal("appliedTargets" in decoded, false);

assert.throws(
  () => decodeHostManagerRollbackState({
    version: 3,
    coordinator: null,
    appliedPlacements: null
  }),
  /Invalid appliedPlacements/);
assert.throws(
  () => decodeHostManagerRollbackState({
    version: 4,
    coordinator: null,
    appliedPlacements: []
  }),
  /Unsupported Host Manager state version/);

assert.equal("message" in decoded, false);
assert.equal("displayName" in decoded.appliedPlacements[0]!, false);
assert.equal(decoded.appliedPlacements[0]?.processId, 42);
assert.equal(decoded.appliedPlacements[0]?.processStartKey, "123");

// 智能调度记录的历史身份（docs/report_lifecycle_2026-10-28.md）：序号、状态、记录时间同有同无。
const records = decoded.appliedPlacements[0]!.records;
assert.deepEqual(records.map(({ sequence, state, recordedAt }) => ({ sequence, state, recordedAt })), [
  { sequence: null, state: null, recordedAt: null },
  { sequence: 42, state: "active", recordedAt: "2026-07-24T12:00:01Z" }
]);
const withRecord = (record: Record<string, unknown>) => ({
  version: 3, coordinator: null,
  appliedPlacements: [{ targetId: "target", processId: null, softwareId: null, resourceKind: "cpu",
    appliedAt: "2026-07-24T12:00:00Z", updatedAt: "2026-07-24T12:00:00Z",
    records: [{ kind: "cpu_affinity", recordId: "r", ...record }] }]
});
for (const [record, error] of [
  [{}, /Invalid sequence/],
  [{ sequence: 0, state: "active", recordedAt: "2026-07-24T12:00:00Z" }, /Invalid sequence/],
  [{ sequence: 1.5, state: "active", recordedAt: "2026-07-24T12:00:00Z" }, /Invalid sequence/],
  [{ sequence: 3, state: "history", recordedAt: "2026-07-24T12:00:00Z" }, /Invalid state/],
  [{ sequence: 3, state: null, recordedAt: "2026-07-24T12:00:00Z" }, /Invalid state/],
  [{ sequence: 3, state: "active", recordedAt: null }, /Invalid recordedAt/],
  [{ sequence: null, state: "active", recordedAt: null }, /Invalid state/],
  [{ sequence: null, state: null, recordedAt: "2026-07-24T12:00:00Z" }, /Invalid recordedAt/]
] as const) {
  assert.throws(() => decodeHostManagerRollbackState(withRecord(record)), error, JSON.stringify(record));
}
