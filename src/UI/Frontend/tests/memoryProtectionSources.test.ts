import assert from "node:assert/strict";
import { createFrontendSources } from "../src/frontendRuntime/source/FrontendSources.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import { memoryProtectionDemand } from "../src/data/memoryProtection/memoryProtectionDemand.ts";
import { memoryStatus, memoryPoint } from "./fixtures/memoryProtection.ts";

const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const channel = new BackendSubscriptionChannel({ backendSession: session });
const timers = new Map<object, { callback: () => void; delay: number }>();
const registry = new SourceRegistry(session, {
  schedule: (callback, delay) => { const id = {}; timers.set(id, { callback, delay }); return id; },
  cancelSchedule: id => { timers.delete(id as object); }
});
const calls: string[] = []; let fail = false; let value: unknown = memoryStatus;
const client = new RequestClient(session, { transport: async url => {
  calls.push(url); if (fail) throw new Error("fixture read failure"); return value;
} });
try {
  const sources = createFrontendSources(client, registry, channel);
  const demand = memoryProtectionDemand(true, { monitor: 750, resourceTable: 900, localSystem: 2875 });
  assert.deepEqual(demand, { active: true, refreshIntervalMs: 2875 });
  const a = sources.memoryProtection.acquire({ active: false });
  const b = sources.memoryProtection.acquire({ active: false });
  assert.equal(calls.length, 0); assert.equal(timers.size, 0);
  assert.equal(a.snapshot.status, "idle");
  a.setDemand(demand); b.setDemand(demand);
  await Promise.all([a.refresh(), b.refresh()]);
  assert.equal(calls.length, 1, "multiple consumers share a single request");
  assert.equal(calls[0], "/api/memory-protection");
  assert.deepEqual(a.snapshot.data, memoryStatus);
  assert.equal(timers.size, 1);
  assert.equal([...timers.values()][0].delay, 2875, "only the named existing preference drives polling");
  const accepted = a.snapshot;
  a.setDemand({ active: false }); b.setDemand({ active: false });
  assert.equal(timers.size, 0); assert.equal(a.snapshot, accepted, "hide retains entire current snapshot");
  a.release(); b.release();
  const resumed = sources.memoryProtection.acquire({ active: false });
  assert.equal(resumed.snapshot, accepted); assert.equal(calls.length, 1, "snapshot reads never sample");
  fail = true;
  assert.equal((await resumed.refresh()).status, "error");
  assert.equal(resumed.snapshot.data, null, "failure replaces the current payload");
  fail = false; value = { ...memoryStatus, actions: [], excludedCandidates: [] };
  assert.equal((await resumed.refresh()).status, "ready");
  assert.deepEqual(resumed.snapshot.data?.actions, []);
  const refreshed = resumed.snapshot;
  resumed.release();
  const again = sources.memoryProtection.acquire({ active: false });
  assert.equal(again.snapshot, refreshed); again.release();
  value = null;
  const point = sources.normalRunningPoint.acquire({ active: false });
  assert.equal(point.snapshot.status, "idle");
  assert.equal((await point.refresh()).status, "ready", "literal null is an accepted empty point");
  assert.equal(point.snapshot.data, null);
  value = memoryPoint;
  assert.deepEqual((await point.refresh()).data, memoryPoint);
  assert.equal(calls.at(-1), "/api/memory-protection/normal-running-point");
  const currentPoint = point.snapshot;
  point.release();
  const next = sources.normalRunningPoint.acquire({ active: false });
  assert.equal(next.snapshot, currentPoint); next.release();
} finally { registry.dispose(); channel.dispose(); session.dispose(); }
assert.equal(timers.size, 0);
console.log("memory protection registry current values and demand-driven polling: passed");
