import assert from "node:assert/strict";
import { getSmartRecordHistory, smartRecordRestoreResults } from "../src/data/optimization/smartRecordHistory.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

/*
 * 智能调度历史（docs/report_lifecycle_2026-10-28.md“智能调度记录”）：
 * GET /api/optimization/smart/records/history?beforeSequence=&limit=，回复 { records, nextBeforeSequence }，
 * 记录按序号严格从新到旧；每条是一条记录加它所属放置的上下文和恢复时间、恢复结果。
 */
const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:smart-record-history-test"
});
const client = new RequestClient(owner);

const entry = (sequence: number, extra: Record<string, unknown> = {}) => ({
  sequence, state: "history", kind: "cpu_affinity", recordId: `record-${sequence}`, targetId: "software:editor",
  softwareId: "editor", resourceKind: "cpu", appliedAt: "2026-10-28T10:00:00Z", processId: 1234,
  processStartKey: "134000000000000000", processName: "editor", recordedAt: "2026-10-28T10:00:01Z",
  restoredAt: "2026-10-28T11:00:00Z", restoreResult: "restored", ...extra
});

try {
  const urls: string[] = [];
  let reply: unknown = { records: [entry(42), entry(40, { softwareId: null, processId: null, processStartKey: null, processName: null })], nextBeforeSequence: 40 };
  globalThis.fetch = (async (input) => {
    urls.push(String(input));
    return new Response(JSON.stringify(reply), { status: 200, headers: { "Content-Type": "application/json" } });
  }) as typeof fetch;

  const first = await getSmartRecordHistory(client, { beforeSequence: null, limit: 50 });
  assert.deepEqual(first.records.map((record) => record.sequence), [42, 40]);
  assert.equal(first.records[1].processId, null);
  assert.equal(first.nextBeforeSequence, 40);
  reply = { records: [], nextBeforeSequence: null };
  assert.deepEqual(await getSmartRecordHistory(client, { beforeSequence: 40, limit: 50 }), { records: [], nextBeforeSequence: null });
  assert.deepEqual(urls, [
    "/api/optimization/smart/records/history?limit=50",
    "/api/optimization/smart/records/history?limit=50&beforeSequence=40"
  ]);

  for (const result of smartRecordRestoreResults) {
    reply = { records: [entry(1, { restoreResult: result })], nextBeforeSequence: null };
    assert.equal((await getSmartRecordHistory(client, { beforeSequence: null, limit: 1 })).records[0].restoreResult, result);
  }

  for (const invalid of [
    { records: [entry(3), entry(3)], nextBeforeSequence: null },
    { records: [entry(3), entry(5)], nextBeforeSequence: null },
    { records: [entry(0)], nextBeforeSequence: null },
    { records: [entry(3, { state: "active" })], nextBeforeSequence: null },
    { records: [entry(3, { restoreResult: "trusted" })], nextBeforeSequence: null },
    { records: [entry(3, { restoredAt: null })], nextBeforeSequence: null },
    { records: [entry(3)], nextBeforeSequence: 0 },
    { records: [entry(3)] }
  ]) {
    reply = invalid;
    await assert.rejects(
      getSmartRecordHistory(client, { beforeSequence: null, limit: 50 }),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "invalid-response"
        && error.attempt.decoderId === "smart.records.history.v1",
      `rejects ${JSON.stringify(invalid)}`);
  }
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
