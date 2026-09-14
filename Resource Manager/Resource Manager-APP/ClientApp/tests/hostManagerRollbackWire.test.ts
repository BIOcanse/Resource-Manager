import assert from "node:assert/strict";
import { decodeHostManagerRollbackState } from "../src/api/hostManagerRollbackWire.ts";

const decoded = decodeHostManagerRollbackState({
  version: 2,
  lastRunAt: null,
  lastRestoreAt: "2026-07-24T12:00:00Z",
  message: "智能调度",
  nativeHostSessionIncarnation: 7,
  appliedPlacements: [
    {
      targetId: "target",
      displayName: "Target",
      softwareId: null,
      resourceKind: "gpu",
      appliedAt: "2026-07-24T12:00:00Z",
      updatedAt: "2026-07-24T12:01:00Z",
      records: [
        {
          kind: "GpuPreference",
          recordId: "record",
          metadata: { result: "ok" }
        }
      ]
    }
  ]
});

assert.equal(decoded.version, 2);
assert.equal(decoded.appliedPlacements.length, 1);
assert.equal(decoded.appliedPlacements[0]?.records[0]?.metadata?.result, "ok");
assert.equal("pendingChanges" in decoded, false);
assert.equal("appliedTargets" in decoded, false);

assert.throws(
  () => decodeHostManagerRollbackState({
    version: 2,
    message: "invalid",
    appliedPlacements: null
  }),
  /Invalid appliedPlacements/);
assert.throws(
  () => decodeHostManagerRollbackState({
    version: 3,
    message: "future",
    appliedPlacements: []
  }),
  /Unsupported Host Manager state version/);
