import assert from "node:assert/strict";
import { createFrontendSources } from "../src/frontendRuntime/source/FrontendSources.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import type { BackendSubscriptionChannel, SubscriptionAddress } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import type { ResponseDecoder } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";
import { applyMemoryProtectionFrame } from "../src/data/memoryProtection/memoryProtectionSubscriptionSource.ts";
import type { HostMemoryProtectionView } from "../src/data/memoryProtection/memoryProtectionTypes.ts";
import { memoryAction, memoryLiveStatus, memoryPoint, memoryTreeChild, memoryTreeRootRecord } from "./fixtures/memoryProtection.ts";

// One subscription carries status, records, record and normalRunningPoint frames; one source owns the folded value.
const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
type Lease = { key: string; path: string; decoder: ResponseDecoder<unknown>; stopped: boolean;
  publish: (value: SourceSnapshot<unknown>) => void };
const leases: Lease[] = [];
const fakeChannel = {
  backendEpoch: "epoch-1",
  subscribe(key: string, address: SubscriptionAddress, decoder: ResponseDecoder<unknown>, publish: (value: SourceSnapshot<unknown>) => void) {
    const lease: Lease = { key, path: address.path, decoder, publish, stopped: false }; leases.push(lease);
    return { update(next: SubscriptionAddress) { lease.path = next.path; }, dispose() { lease.stopped = true; } };
  },
  dispose() {}
};
const channel = fakeChannel as unknown as BackendSubscriptionChannel;
const registry = new SourceRegistry(session, { schedule: () => ({}), cancelSchedule: () => {} });
const calls: string[] = [];
const client = new RequestClient(session, { transport: async url => { calls.push(url); return null; } });
const snapshot = (lease: Lease, fields: Partial<SourceSnapshot<unknown>>): SourceSnapshot<unknown> => ({ key: lease.key,
  status: "ready", data: null, error: null, backendEpoch: fakeChannel.backendEpoch, revision: 1, acceptedAttempt: null,
  domainRevision: null, capturedAt: null, ...fields });
const frame = (lease: Lease, payload: unknown) => lease.publish(snapshot(lease, { data: lease.decoder.decode(payload) }));
const record = (actionId: string, processId: number, fields: Record<string, unknown> = {}) =>
  ({ ...memoryAction, actionId, processId, processStartKey: `${processId}0`, ...fields });
try {
  const sources = createFrontendSources(client, registry, channel);
  const source = sources.memoryProtection;
  const view = () => source.readSnapshot().data;
  const ids = () => view()?.records.map(row => [row.actionId, row.processId, row.outcome]);
  assert.equal(source.readSnapshot().status, "loading", "nothing is read before demand");
  assert.equal(leases.length, 0);
  const seen: SourceSnapshot<HostMemoryProtectionView>[] = [];
  const stopA = source.subscribeSnapshot(value => seen.push(value));
  const stopB = source.subscribeSnapshot(() => {});
  assert.equal(leases.length, 1, "consumers share one logical subscription");
  assert.equal(leases[0].path, "/api/memory-protection/subscribe");
  const lease = leases[0];

  // Replay: status, records, point. The view is ready only once all three have arrived.
  frame(lease, { type: "status", status: memoryLiveStatus });
  assert.equal(source.readSnapshot().status, "loading", "status alone is not the whole value");
  frame(lease, { type: "records", records: [record("b".repeat(32), 2), record("a".repeat(32), 1)], limit: 2 });
  assert.equal(source.readSnapshot().status, "loading");
  frame(lease, { type: "normalRunningPoint", normalRunningPoint: null });
  assert.equal(source.readSnapshot().status, "ready");
  assert.deepEqual(view(), { status: memoryLiveStatus, records: [record("b".repeat(32), 2), record("a".repeat(32), 1)],
    recordLimit: 2, normalRunningPoint: null });
  assert.deepEqual(seen.at(-1)?.data, view(), "listeners receive the folded value");

  // record: a new record is the newest and the list keeps the newest limit; the same action and process replaces in place.
  frame(lease, { type: "record", record: record("c".repeat(32), 3) });
  assert.deepEqual(ids(), [["c".repeat(32), 3, "completed"], ["b".repeat(32), 2, "completed"]], "truncated to the limit");
  frame(lease, { type: "record", record: record("b".repeat(32), 2, { outcome: "termination-failed", reason: "termination-failed",
    detail: "terminate-failed-5" }) });
  assert.deepEqual(ids(), [["c".repeat(32), 3, "completed"], ["b".repeat(32), 2, "termination-failed"]], "updated in place");
  frame(lease, { type: "record", record: record("c".repeat(32), 4) });
  assert.deepEqual(ids(), [["c".repeat(32), 4, "completed"], ["c".repeat(32), 3, "completed"]],
    "another process of the same action is a separate record");
  frame(lease, { type: "record", record: memoryTreeChild });
  frame(lease, { type: "record", record: memoryTreeRootRecord });
  assert.deepEqual(view()?.records, [memoryTreeRootRecord, memoryTreeChild], "tree members fold like any record");

  // normalRunningPoint: the full point is replaced on create and replace, and null clears it.
  frame(lease, { type: "normalRunningPoint", normalRunningPoint: memoryPoint });
  assert.deepEqual(view()?.normalRunningPoint, memoryPoint);
  frame(lease, { type: "status", status: { ...memoryLiveStatus, decisionReason: "below-threshold" } });
  assert.equal(view()?.status.decisionReason, "below-threshold");
  assert.deepEqual(view()?.normalRunningPoint, memoryPoint, "a status frame leaves records and the point unchanged");
  assert.equal(view()?.records.length, 2);
  frame(lease, { type: "normalRunningPoint", normalRunningPoint: null });
  assert.equal(view()?.normalRunningPoint, null, "a cleared point is null");
  assert.equal(calls.length, 0, "nothing is polled");

  // A failure clears the fold: later frames cannot be trusted until the replay that a new subscription receives.
  const failure = new Error("frame could not be decoded");
  lease.publish(snapshot(lease, { status: "error", error: failure }));
  assert.deepEqual([source.readSnapshot().status, source.readSnapshot().data, source.readSnapshot().error], ["error", null, failure]);
  frame(lease, { type: "status", status: memoryLiveStatus });
  frame(lease, { type: "record", record: record("d".repeat(32), 5) });
  assert.equal(source.readSnapshot().status, "error", "frames after a failure do not resume a partial value");
  frame(lease, { type: "records", records: [record("d".repeat(32), 5)], limit: 64 });
  frame(lease, { type: "normalRunningPoint", normalRunningPoint: memoryPoint });
  assert.equal(source.readSnapshot().status, "ready", "the replay restores the value");
  assert.deepEqual(ids(), [["d".repeat(32), 5, "completed"]]);
  assert.throws(() => lease.decoder.decode({ type: "status", status: { ...memoryLiveStatus, active: true } }),
    "active must agree with inactiveReason");

  // A new backend session fences the previous fold.
  fakeChannel.backendEpoch = "epoch-2";
  frame(lease, { type: "status", status: memoryLiveStatus });
  assert.equal(source.readSnapshot().status, "loading", "records and point of the previous session are dropped");
  frame(lease, { type: "records", records: [], limit: 64 });
  frame(lease, { type: "normalRunningPoint", normalRunningPoint: null });
  assert.deepEqual(view(), { status: memoryLiveStatus, records: [], recordLimit: 64, normalRunningPoint: null });

  stopA(); stopB();
  assert.equal(lease.stopped, true, "the subscription closes with its last consumer");
  const kept = view();
  assert.deepEqual(kept, { status: memoryLiveStatus, records: [], recordLimit: 64, normalRunningPoint: null },
    "closing keeps the current value readable");
  const again: SourceSnapshot<HostMemoryProtectionView>[] = [];
  const stopC = source.subscribeSnapshot(value => again.push(value));
  assert.equal(leases.length, 2, "a new consumer opens a new subscription");
  assert.deepEqual(again.at(-1)?.data, kept, "the last folded frame is not applied twice");
  stopC();

  // The fold itself: a record before the records replay has no list to join.
  assert.deepEqual(applyMemoryProtectionFrame({}, { type: "record", record: memoryAction as never }), {});
  assert.deepEqual(applyMemoryProtectionFrame({}, { type: "records", records: [memoryAction, memoryAction] as never, limit: 1 }).records,
    [memoryAction], "a replay longer than its limit keeps the newest");
} finally { registry.dispose(); session.dispose(); }
console.log("memory protection subscription source: passed");
