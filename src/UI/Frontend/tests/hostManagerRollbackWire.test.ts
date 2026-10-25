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
          metadata: { result: "ok" }
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
