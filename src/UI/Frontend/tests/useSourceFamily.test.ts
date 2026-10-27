import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import type { SourceDemand, SourceFamily, SourceLease } from "../src/frontendRuntime/source/SourceDescriptor.ts";
import { useSourceFamily } from "../src/frontendRuntime/source/useSourceFamily.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";

const values = new Map<string, SourceSnapshot<number>>([
  ["a", snapshot("a", "ready", 42)],
  ["b", snapshot("b", "ready", 17)]
]);
const leases: ReturnType<typeof fakeLease>[] = [];
const [internal, setInternal] = createSignal(0);
const family: SourceFamily<string, number> = {
  key: "test.family-hook",
  acquire(query) {
    internal();
    const owned = fakeLease(query);
    leases.push(owned);
    return owned.lease;
  }
};
let queryReads = 0;
const state = createRoot(dispose => {
  const [query, setQuery] = createSignal("a");
  const [active, setActive] = createSignal(false);
  const [interval, setInterval] = createSignal<number | null>(null);
  const view = useSourceFamily(family, () => { queryReads += 1; return query(); },
    () => ({ active: active(), refreshIntervalMs: interval() }));
  assert.strictEqual(view.snapshot(), values.get("a"), "Warm first read is available before effects");
  assert.equal(leases.length, 1, "Initial bind acquires only one lease");
  setQuery("b");
  assert.strictEqual(view.snapshot(), values.get("b"), "Query changes bind synchronously before effects");
  setQuery("cold");
  assert.equal(view.snapshot().status, "idle", "A cold query uses its own pending value");
  assert.equal(view.snapshot().data, null);
  setQuery("a");
  assert.equal(view.snapshot().data, 42);
  return { dispose, view, setQuery, setActive, setInterval };
});

try {
  const warm = leases.at(-1)!;
  assert(leases.slice(0, -1).every(owned => owned.releases === 1));
  const readsBeforeInternal = queryReads;
  const demandsBeforeInternal = warm.demands.length;
  setInternal(1);
  assert.equal(queryReads, readsBeforeInternal, "Registry internal signals are not tracked");
  assert.equal(warm.demands.length, demandsBeforeInternal);
  state.setActive(true);
  state.setInterval(250);
  assert.equal(leases.at(-1), warm, "Demand changes reuse the query lease");
  assert.deepEqual(warm.demands.at(-1), { active: true, refreshIntervalMs: 250 });
  assert.equal(state.view.acceptAuthoritative(99).data, 99);
  assert.equal(state.view.snapshot().data, 99);
  state.setQuery("b");
  assert.equal(warm.releases, 1);
  warm.publishLate(snapshot("a", "ready", 123));
  assert.equal(state.view.snapshot().data, 17, "Late callbacks from a released query are fenced");
  assert.strictEqual(await state.view.refresh(), values.get("b"));
  assert.strictEqual(await state.view.invalidate(), values.get("b"));
  state.setActive(false);
  assert.equal(state.view.snapshot().data, 17, "Inactive demand keeps the accepted value");
} finally {
  state.dispose();
}
assert(leases.every(owned => owned.releases === 1), "Every acquired lease is released exactly once");
const beforeLate = state.view.snapshot();
leases.at(-1)!.publishLate(snapshot("b", "error", null));
assert.strictEqual(state.view.snapshot(), beforeLate, "Disposal fences late publication");
console.log("Source family hook: synchronous warm reads, query switching, demand, dependency isolation and cleanup passed.");

function fakeLease(key: string) {
  let listener: ((value: SourceSnapshot<number>) => void) | undefined;
  let releases = 0;
  const demands: SourceDemand[] = [];
  const lease: SourceLease<number> = {
    key,
    get snapshot() { return values.get(key) ?? snapshot(key, "idle", null); },
    subscribe(next) {
      internal(); listener = next;
      // Keep the captured callback to exercise the binding's own cleanup fence.
      return () => {};
    },
    setDemand(demand) { internal(); demands.push(demand); },
    refresh: async () => lease.snapshot,
    invalidate: async () => lease.snapshot,
    acceptAuthoritative(value) {
      const next = snapshot(key, "ready", value);
      values.set(key, next); listener?.(next); return next;
    },
    release() { releases += 1; }
  };
  return { lease, demands, get releases() { return releases; },
    publishLate: (value: SourceSnapshot<number>) => listener?.(value) };
}

function snapshot(key: string, status: SourceSnapshot<number>["status"], data: number | null): SourceSnapshot<number> {
  return { key, status, data, error: null, backendEpoch: "one", revision: 1,
    acceptedAttempt: null, domainRevision: null, capturedAt: null };
}
