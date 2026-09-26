import assert from "node:assert/strict";
import type { BackendSessionSnapshot } from "../src/frontendRuntime/session/backendSessionTypes.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import {
  canonicalizeSourceQuery,
  SourceRegistry
} from "../src/frontendRuntime/source/SourceRegistry.ts";

class FakeBackendSession {
  private readonly listeners = new Set<(snapshot: BackendSessionSnapshot) => void>();
  snapshot: BackendSessionSnapshot = readySnapshot("epoch-a", 1);
  retryCalls = 0;
  retryHandler: (() => Promise<void>) | null = null;

  subscribe(listener: (snapshot: BackendSessionSnapshot) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  publish(snapshot: BackendSessionSnapshot): void {
    this.snapshot = snapshot;
    for (const listener of this.listeners) {
      listener(snapshot);
    }
  }

  async retry(): Promise<void> {
    this.retryCalls += 1;
    await this.retryHandler?.();
  }
}

class FakeClock {
  now = 0;
  private nextId = 0;
  private readonly tasks = new Map<number, { due: number; callback: () => void }>();

  schedule = (callback: () => void, delayMs: number): number => {
    const id = ++this.nextId;
    this.tasks.set(id, { due: this.now + delayMs, callback });
    return id;
  };

  cancel = (handle: unknown): void => {
    this.tasks.delete(handle as number);
  };

  advance(milliseconds: number): void {
    const target = this.now + milliseconds;
    while (true) {
      let selectedId: number | null = null;
      let selectedDue = Number.POSITIVE_INFINITY;
      for (const [id, task] of this.tasks) {
        if (task.due <= target && task.due < selectedDue) {
          selectedId = id;
          selectedDue = task.due;
        }
      }
      if (selectedId === null) {
        break;
      }
      const task = this.tasks.get(selectedId)!;
      this.tasks.delete(selectedId);
      this.now = task.due;
      task.callback();
    }
    this.now = target;
  }
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function createRegistry(session: FakeBackendSession, clock = new FakeClock()) {
  return {
    clock,
    registry: new SourceRegistry(session, {
      schedule: clock.schedule,
      cancelSchedule: clock.cancel,
      recordRetentionMs: 25
    })
  };
}

{
  const session = new FakeBackendSession();
  let monotonic = 100;
  const observations: Array<{
    phase: string;
    outcome: string | null;
    inFlightCount: number;
    durationMs: number | null;
  }> = [];
  const registry = new SourceRegistry(session, {
    recordRetentionMs: 25,
    monotonicNow: () => ++monotonic,
    onActivity: (observation) => observations.push({
      phase: observation.phase,
      outcome: observation.outcome,
      inFlightCount: observation.inFlightCount,
      durationMs: observation.durationMs
    })
  });
  const source = registry.define({
    key: "performance-observation",
    load: async () => ({ value: 1 })
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  assert.deepEqual(observations, [
    { phase: "started", outcome: null, inFlightCount: 1, durationMs: null },
    { phase: "settled", outcome: "ready", inFlightCount: 0, durationMs: 1 }
  ]);
  lease.acceptAuthoritative({ value: 2 });
  assert.deepEqual(observations.at(-1), {
    phase: "authoritative",
    outcome: "accepted-authoritative",
    inFlightCount: 0,
    durationMs: 0
  });
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const registry = new SourceRegistry(session, {
    onActivity: () => { throw new Error("diagnostic observer failed"); }
  });
  const source = registry.define({
    key: "performance-observer-isolation",
    load: async () => ({ value: 1 })
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  assert.equal(lease.snapshot.status, "ready");
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  let diagnosticClockReads = 0;
  const registry = new SourceRegistry(session, {
    monotonicNow: () => {
      diagnosticClockReads += 1;
      return diagnosticClockReads;
    }
  });
  const source = registry.define({
    key: "disabled-performance-observer",
    load: async () => ({ value: 1 })
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  assert.equal(lease.snapshot.status, "ready");
  assert.equal(diagnosticClockReads, 0);
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const first = deferred<{ value: number }>();
  let loads = 0;
  const source = registry.define({
    key: "shared",
    load: () => {
      loads += 1;
      return first.promise;
    }
  });
  const leaseA = source.acquire({ active: true, refreshIntervalMs: null });
  const leaseB = source.acquire({ active: true, refreshIntervalMs: null });
  assert.equal(loads, 0);
  await Promise.resolve();
  assert.equal(loads, 1);
  const refreshA = leaseA.refresh();
  const refreshB = leaseB.refresh();
  first.resolve({ value: 7 });
  await Promise.all([refreshA, refreshB]);
  assert.equal(loads, 1);
  assert.equal(leaseA.snapshot.status, "ready");
  assert.equal(leaseA.snapshot, leaseB.snapshot);
  assert.deepEqual(leaseA.snapshot.data, { value: 7 });
  leaseA.release();
  assert.equal(leaseB.snapshot.status, "ready");
  leaseB.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const oldRead = deferred<{ revision: number; value: string }>();
  const source = registry.define({
    key: "authoritative-command-result",
    load: () => oldRead.promise,
    selectDomainRevision: (value) => value.revision
  });
  const first = source.acquire({ active: true, refreshIntervalMs: null });
  const second = source.acquire({ active: false, refreshIntervalMs: null });
  await Promise.resolve();
  assert.equal(first.snapshot.status, "loading");

  const accepted = first.acceptAuthoritative({ revision: 2, value: "command" });
  assert.equal(accepted.status, "ready");
  assert.equal(accepted.acceptedAttempt, 2);
  assert.equal(first.snapshot, second.snapshot);
  assert.deepEqual(second.snapshot.data, { revision: 2, value: "command" });

  oldRead.resolve({ revision: 1, value: "late-read" });
  await flushPromises();
  assert.deepEqual(first.snapshot.data, { revision: 2, value: "command" });
  assert.equal(first.snapshot.domainRevision, 2);
  assert.equal(
    first.acceptAuthoritative({ revision: 1, value: "older-command" }),
    first.snapshot);
  assert.deepEqual(first.snapshot.data, { revision: 2, value: "command" });
  first.release();
  second.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const listenerErrors: Array<{ error: unknown; sourceKey: string }> = [];
  const registry = new SourceRegistry(session, {
    recordRetentionMs: 25,
    onListenerError: (error, sourceKey) => listenerErrors.push({ error, sourceKey })
  });
  const source = registry.define({
    key: "listener-isolation",
    load: async () => ({ value: 1 })
  });
  const first = source.acquire({ active: false });
  const second = source.acquire({ active: false });
  let observedStatus: string | null = null;
  first.subscribe(() => {
    throw new Error("consumer failed");
  });
  second.subscribe((snapshot) => {
    observedStatus = snapshot.status;
  });
  await first.refresh();
  assert.equal(first.snapshot.status, "ready");
  assert.equal(second.snapshot.status, "ready");
  assert.equal(observedStatus, "ready");
  assert.equal(listenerErrors.length, 2);
  assert.ok(listenerErrors.every((item) => item.sourceKey === "listener-isolation"));
  first.release();
  second.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  let loads = 0;
  let fail = false;
  const source = registry.define({
    key: "stale",
    load: async () => {
      loads += 1;
      if (fail) {
        throw new Error("temporary failure");
      }
      return { value: loads };
    }
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  assert.equal(lease.snapshot.status, "ready");
  fail = true;
  await lease.refresh();
  assert.equal(lease.snapshot.status, "error");
  assert.equal(lease.snapshot.data, null);
  lease.setDemand({ active: false, refreshIntervalMs: null });
  assert.equal(lease.snapshot.status, "idle");
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  let loads = 0;
  let fail = false;
  const source = registry.define({
    key: "authoritative-current",
    retainLastGood: false,
    load: async () => {
      loads += 1;
      if (fail) {
        throw new Error("read failed");
      }
      return { value: loads };
    }
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  assert.equal(lease.snapshot.status, "ready");
  assert.deepEqual(lease.snapshot.data, { value: 1 });

  fail = true;
  await lease.refresh();
  assert.equal(lease.snapshot.status, "error");
  assert.equal(lease.snapshot.data, null);

  fail = false;
  await lease.refresh();
  assert.equal(lease.snapshot.status, "ready");
  assert.deepEqual(lease.snapshot.data, { value: 3 });

  lease.setDemand({ active: false, refreshIntervalMs: null });
  assert.equal(lease.snapshot.status, "idle");
  assert.equal(lease.snapshot.data, null);
  lease.setDemand({ active: true, refreshIntervalMs: null });
  assert.equal(lease.snapshot.status, "loading");
  await flushPromises();
  assert.equal(lease.snapshot.status, "ready");
  assert.deepEqual(lease.snapshot.data, { value: 4 });
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const next = deferred<{ value: number }>();
  let loads = 0;
  const source = registry.define({
    key: "no-frontend-sampling-cache",
    retainLastGood: false,
    load: async () => {
      loads += 1;
      return loads === 1 ? { value: 1 } : next.promise;
    }
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  assert.deepEqual(lease.snapshot.data, { value: 1 });

  const refresh = lease.refresh();
  assert.equal(lease.snapshot.status, "refreshing");
  assert.deepEqual(lease.snapshot.data, { value: 1 });
  next.resolve({ value: 2 });
  await refresh;
  assert.equal(lease.snapshot.status, "ready");
  assert.deepEqual(lease.snapshot.data, { value: 2 });
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  session.snapshot = unavailableSnapshot(1);
  session.retryHandler = async () => {
    session.publish(readySnapshot("epoch-b", 2));
  };
  const { registry } = createRegistry(session);
  let loads = 0;
  const source = registry.define({
    key: "manual-session-retry",
    load: async () => ({ value: ++loads })
  });
  const lease = source.acquire({ active: false, refreshIntervalMs: null });
  assert.equal(lease.snapshot.status, "unavailable");
  await lease.refresh();
  assert.equal(session.retryCalls, 1);
  assert.equal(loads, 1);
  assert.equal(lease.snapshot.status, "ready");
  assert.deepEqual(lease.snapshot.data, { value: 1 });
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const epochA = deferred<{ epoch: string }>();
  const epochB = deferred<{ epoch: string }>();
  let loads = 0;
  const source = registry.define({
    key: "epoch-fence",
    load: () => {
      loads += 1;
      return loads === 1 ? epochA.promise : epochB.promise;
    }
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await Promise.resolve();
  assert.equal(loads, 1);
  session.publish(readySnapshot("epoch-b", 2));
  await Promise.resolve();
  assert.equal(loads, 2);
  epochB.resolve({ epoch: "epoch-b" });
  await flushPromises();
  epochA.resolve({ epoch: "epoch-a" });
  await flushPromises();
  assert.equal(lease.snapshot.backendEpoch, "epoch-b");
  assert.deepEqual(lease.snapshot.data, { epoch: "epoch-b" });
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const values = [
    { revision: 2, value: "new" },
    { revision: 1, value: "old" }
  ];
  const source = registry.define({
    key: "domain-revision",
    retainLastGood: true,
    load: async () => values.shift()!,
    selectDomainRevision: (value) => value.revision
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  await lease.refresh();
  assert.equal(lease.snapshot.status, "ready");
  assert.equal(lease.snapshot.domainRevision, 2);
  assert.deepEqual(lease.snapshot.data, { revision: 2, value: "new" });
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const pending = deferred<{ value: number }>();
  let loadCount = 0;
  const source = registry.define({
    key: "inactive-settles",
    load: async (signal) => {
      loadCount += 1;
      if (loadCount === 1) {
        return { value: 1 };
      }
      return new Promise<{ value: number }>((resolve, reject) => {
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
        pending.promise.then(resolve, reject);
      });
    }
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await flushPromises();
  assert.equal(lease.snapshot.status, "ready");
  const refresh = lease.refresh();
  await Promise.resolve();
  assert.equal(lease.snapshot.status, "refreshing");
  assert.deepEqual(lease.snapshot.data, { value: 1 });
  lease.setDemand({ active: false, refreshIntervalMs: null });
  await refresh;
  assert.equal(lease.snapshot.status, "idle");
  assert.equal(lease.snapshot.data, null);
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const source = registry.define({
    key: "inactive-without-data",
    load: (signal) => new Promise<never>((_resolve, reject) => {
      signal.addEventListener("abort", () => reject(signal.reason), { once: true });
    })
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await Promise.resolve();
  assert.equal(lease.snapshot.status, "loading");
  lease.setDemand({ active: false, refreshIntervalMs: null });
  await flushPromises();
  assert.equal(lease.snapshot.status, "idle");
  assert.equal(lease.snapshot.data, null);
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry, clock } = createRegistry(session);
  let aborted = false;
  const source = registry.define({
    key: "cleanup",
    load: (signal) => new Promise<never>((_resolve, reject) => {
      signal.addEventListener("abort", () => {
        aborted = true;
        reject(signal.reason);
      }, { once: true });
    })
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: null });
  await Promise.resolve();
  assert.equal(registry.recordCount, 1);
  lease.release();
  await flushPromises();
  assert.equal(aborted, true);
  clock.advance(25);
  assert.equal(registry.recordCount, 0);
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry, clock } = createRegistry(session);
  let loads = 0;
  const source = registry.define({
    key: "retry-backoff",
    retryPolicy: {
      initialDelayMs: 1_000,
      maximumDelayMs: 8_000,
      multiplier: 2
    },
    load: async () => {
      loads += 1;
      if (loads <= 3) {
        throw new Error("temporary");
      }
      return { value: loads };
    }
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: 1_000 });
  await flushPromises();
  assert.equal(loads, 1);
  clock.advance(999);
  await flushPromises();
  assert.equal(loads, 1);
  clock.advance(1);
  await flushPromises();
  assert.equal(loads, 2);
  clock.advance(1_999);
  await flushPromises();
  assert.equal(loads, 2);
  clock.advance(1);
  await flushPromises();
  assert.equal(loads, 3);
  clock.advance(3_999);
  await flushPromises();
  assert.equal(loads, 3);
  clock.advance(1);
  await flushPromises();
  assert.equal(loads, 4);
  assert.equal(lease.snapshot.status, "ready");
  clock.advance(999);
  await flushPromises();
  assert.equal(loads, 4);
  clock.advance(1);
  await flushPromises();
  assert.equal(loads, 5);
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry, clock } = createRegistry(session);
  let loads = 0;
  const source = registry.define({
    key: "non-retryable-pause",
    retryPolicy: {
      initialDelayMs: 100,
      maximumDelayMs: 1_000
    },
    load: async () => {
      loads += 1;
      throw nonRetryableProblem();
    }
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: 10 });
  await flushPromises();
  assert.equal(loads, 1);
  clock.advance(10_000);
  await flushPromises();
  assert.equal(loads, 1);
  await lease.refresh();
  assert.equal(loads, 2);
  clock.advance(10_000);
  await flushPromises();
  assert.equal(loads, 2);
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry, clock } = createRegistry(session);
  let loads = 0;
  const source = registry.define({
    key: "stable-demand-schedule",
    load: async () => ({ value: ++loads })
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: 10 });
  await flushPromises();
  clock.advance(5);
  lease.setDemand({ active: true, refreshIntervalMs: 10 });
  clock.advance(5);
  await flushPromises();
  assert.equal(loads, 2);
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry, clock } = createRegistry(session);
  let loads = 0;
  const source = registry.define({
    key: "scheduled",
    load: async () => ({ value: ++loads })
  });
  const lease = source.acquire({ active: true, refreshIntervalMs: 10 });
  await flushPromises();
  assert.equal(loads, 1);
  clock.advance(10);
  await flushPromises();
  assert.equal(loads, 2);
  lease.setDemand({ active: false, refreshIntervalMs: 10 });
  clock.advance(100);
  await flushPromises();
  assert.equal(loads, 2);
  lease.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  let loads = 0;
  const capturedQueries: Array<{ ids: string[]; mode: string }> = [];
  const metrics = registry.defineFamily<
    { ids: string[]; mode: string },
    { ids: string[]; mode: string }
  >({
    key: "metrics.snapshot",
    load: async (query) => {
      loads += 1;
      capturedQueries.push(query);
      return query;
    }
  });
  const first = metrics.acquire({ ids: ["cpu.usage", "memory.usage"], mode: "summary" });
  const second = metrics.acquire({ mode: "summary", ids: ["cpu.usage", "memory.usage"] });
  await flushPromises();
  assert.equal(loads, 1);
  assert.equal(first.key, second.key);
  assert.equal(first.snapshot, second.snapshot);
  assert.deepEqual(first.snapshot.data, {
    ids: ["cpu.usage", "memory.usage"],
    mode: "summary"
  });
  assert.equal(Object.isFrozen(capturedQueries[0]), true);
  assert.equal(Object.isFrozen(capturedQueries[0].ids), true);
  first.release();
  second.release();
  registry.dispose();
}

{
  const session = new FakeBackendSession();
  const { registry } = createRegistry(session);
  const queries = new Map([
    ["first", deferred<{ query: string }>()],
    ["second", deferred<{ query: string }>()]
  ]);
  const family = registry.defineFamily<{ query: string }, { query: string }>({
    key: "parameter-fence",
    load: (query) => queries.get(query.query)!.promise
  });
  const first = family.acquire({ query: "first" });
  const second = family.acquire({ query: "second" });
  await Promise.resolve();
  assert.equal(registry.recordCount, 2);
  queries.get("second")!.resolve({ query: "second" });
  await flushPromises();
  assert.deepEqual(second.snapshot.data, { query: "second" });
  queries.get("first")!.resolve({ query: "first" });
  await flushPromises();
  assert.deepEqual(first.snapshot.data, { query: "first" });
  assert.deepEqual(second.snapshot.data, { query: "second" });
  first.release();
  second.release();
  registry.dispose();
}

{
  const canonical = canonicalizeSourceQuery({
    z: 1,
    nested: { second: true, first: "value" },
    items: [2, 1]
  });
  assert.equal(
    canonical.key,
    '{"items":[2,1],"nested":{"first":"value","second":true},"z":1}');
  assert.equal(Object.isFrozen(canonical.value), true);
  assert.equal(Object.isFrozen(canonical.value.nested), true);
  assert.equal(Object.isFrozen(canonical.value.items), true);
  assert.throws(() => canonicalizeSourceQuery({ value: Number.POSITIVE_INFINITY }));
  const cyclic: { self?: unknown } = {};
  cyclic.self = cyclic;
  assert.throws(() => canonicalizeSourceQuery(cyclic));
}

function readySnapshot(epoch: string, revision: number): BackendSessionSnapshot {
  return {
    status: "ready",
    session: {
      epoch,
      authority: "standalone-page",
      generation: epoch,
      backendInstanceId: epoch,
      processId: null,
      processStartUtcTicks: null,
      buildVersion: null
    },
    reason: null,
    publicationRevision: null,
    revision
  };
}

function unavailableSnapshot(revision: number): BackendSessionSnapshot {
  return {
    status: "unavailable",
    session: null,
    reason: "Backend unavailable.",
    publicationRevision: null,
    revision
  };
}

async function flushPromises(): Promise<void> {
  for (let index = 0; index < 5; index += 1) {
    await Promise.resolve();
  }
}

function nonRetryableProblem(): RequestProblem {
  return new RequestProblem("not found", {
    kind: "http",
    retryable: false,
    status: 404,
    attempt: {
      id: "fixture",
      sequence: 1,
      key: "fixture",
      decoderId: "fixture",
      method: "GET",
      backendEpoch: "epoch-a",
      startedAt: 0,
      finishedAt: 1,
      durationMs: 1
    }
  });
}
