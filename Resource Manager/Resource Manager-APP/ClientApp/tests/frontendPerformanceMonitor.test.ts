import assert from "node:assert/strict";
import {
  createFrontendPerformanceMonitor,
  type FrontendPerformanceEvent
} from "../src/frontendRuntime/performance/FrontendPerformanceMonitor.ts";
import {
  disabledFrontendPerformanceMonitor,
  frontendPerformanceBootstrapRequested
} from "../src/frontendRuntime/performance/frontendPerformanceBootstrap.ts";

assert.equal(disabledFrontendPerformanceMonitor.enabled, false);
assert.equal(frontendPerformanceBootstrapRequested({}), false);
assert.equal(frontendPerformanceBootstrapRequested({
  __resourceManagerFrontendPerformanceBootstrap: {
    schemaVersion: "1",
    enabled: true,
    capacity: 256
  }
}), false);
assert.equal(frontendPerformanceBootstrapRequested({
  __resourceManagerFrontendPerformanceBootstrap: {
    schemaVersion: 1,
    enabled: true,
    capacity: 256
  }
}), true);

for (const bootstrap of [
  undefined,
  null,
  { schemaVersion: "1", enabled: true, capacity: 256 },
  { schemaVersion: 1, enabled: true, capacity: 255 },
  { schemaVersion: 1, enabled: true, capacity: 256.5 },
  { schemaVersion: 1, enabled: false, capacity: 256 }
]) {
  const exposure: Record<string, unknown> = bootstrap === undefined
    ? {}
    : { __resourceManagerFrontendPerformanceBootstrap: bootstrap };
  const monitor = createFrontendPerformanceMonitor({ exposureTarget: exposure });
  assert.equal(monitor.enabled, false);
  assert.equal(monitor.requestObserver, undefined);
  assert.equal(monitor.sourceObserver, undefined);
  assert.equal(monitor.snapshot(), null);
  assert.equal(exposure.__resourceManagerFrontendPerformance, undefined);
}

{
  let sideEffectCount = 0;
  const monitor = createFrontendPerformanceMonitor({
    exposureTarget: {},
    now: () => {
      sideEffectCount += 1;
      return 1;
    },
    scheduleFrame: () => {
      sideEffectCount += 1;
      return 1;
    },
    createLongTaskObserver: () => {
      sideEffectCount += 1;
      return () => undefined;
    }
  });
  monitor.markLifecycle("ignored");
  monitor.markRouteIntent("monitor");
  monitor.markRouteCommit("monitor");
  monitor.reset();
  monitor.dispose();
  assert.equal(sideEffectCount, 0);
}

{
  let clock = 10;
  const frames: FrameRequestCallback[] = [];
  let longTaskCallback:
    | ((entry: Pick<PerformanceEntry, "name" | "startTime" | "duration">) => void)
    | null = null;
  let disconnected = false;
  const exposure: Record<string, unknown> = {
    __resourceManagerFrontendPerformanceBootstrap: {
      schemaVersion: 1,
      enabled: true,
      capacity: 256
    }
  };
  const monitor = createFrontendPerformanceMonitor({
    exposureTarget: exposure,
    now: () => ++clock,
    scheduleFrame: (callback) => {
      frames.push(callback);
      return frames.length;
    },
    createLongTaskObserver: (callback) => {
      longTaskCallback = callback;
      return () => { disconnected = true; };
    }
  });
  assert.equal(monitor.enabled, true);
  assert.ok(exposure.__resourceManagerFrontendPerformance);

  monitor.markLifecycle("app-render-returned");
  monitor.markRouteIntent("settings");
  monitor.markRouteCommit("settings");
  assert.equal(frames.length, 1);
  frames.shift()!(20);
  assert.equal(frames.length, 1);
  frames.shift()!(30);
  longTaskCallback!({ name: "self", startTime: 31, duration: 55 });
  monitor.requestObserver!({
    outcome: "success",
    attempt: {
      id: "request-1",
      sequence: 1,
      key: "metrics.snapshot",
      decoderId: "metrics.snapshot.v1",
      method: "GET",
      backendEpoch: "epoch-1",
      startedAt: 40,
      finishedAt: 44,
      durationMs: 4
    }
  });
  monitor.sourceObserver!({
    phase: "settled",
    sourceKey: "metrics.snapshot",
    attempt: 2,
    generation: 2,
    trigger: "scheduled",
    outcome: "ready",
    startedAt: 45,
    finishedAt: 47,
    durationMs: 2,
    leaseCount: 2,
    activeLeaseCount: 1,
    inFlightCount: 0
  });
  monitor.recordTaskChange({
    kind: "upsert",
    revision: 1,
    snapshot: {
      id: "task-1",
      descriptor: {
        key: "route-transition",
        scopeKey: "app",
        durability: "transient",
        visibility: "hidden",
        concurrency: "replace"
      },
      generation: 1,
      scopeGeneration: 1,
      status: "running",
      revision: 2,
      progress: null,
      error: null,
      reason: null,
      createdAt: "2026-08-25T00:00:00.000Z",
      startedAt: "2026-08-25T00:00:00.001Z",
      completedAt: null
    }
  });

  const snapshot = monitor.snapshot()!;
  assert.equal(snapshot.schemaVersion, 1);
  assert.equal(snapshot.enabled, true);
  assert.equal(snapshot.capacity, 256);
  assert.equal(snapshot.droppedEventCount, 0);
  assert.deepEqual(
    snapshot.events.filter((event) => event.kind === "route").map((event) => event.phase),
    ["intent", "commit", "frame-1", "frame-2"]);
  assert.equal(snapshot.events.filter((event) => event.kind === "request").length, 1);
  assert.equal(snapshot.events.filter((event) => event.kind === "source").length, 1);
  assert.equal(snapshot.events.filter((event) => event.kind === "task").length, 1);
  assert.equal(snapshot.events.filter((event) => event.kind === "long-task").length, 1);
  assert.ok(snapshot.events.every((event) => !containsSensitiveField(event)));

  for (let index = 0; index < 300; index += 1) {
    monitor.markLifecycle(`bounded-${index}`);
  }
  const overflowed = monitor.snapshot()!;
  assert.equal(overflowed.events.length, 256);
  assert.ok(overflowed.droppedEventCount > 0);
  assert.ok(isStrictlyIncreasing(overflowed.events.map((event) => event.sequence)));

  const publicApi = exposure.__resourceManagerFrontendPerformance as {
    snapshot(): { events: readonly FrontendPerformanceEvent[] };
    reset(): void;
  };
  publicApi.reset();
  assert.equal(publicApi.snapshot().events.length, 0);
  monitor.dispose();
  assert.equal(disconnected, true);
  assert.equal(exposure.__resourceManagerFrontendPerformance, undefined);
}

function containsSensitiveField(value: unknown): boolean {
  return Boolean(value && typeof value === "object" && [
    "url",
    "token",
    "payload",
    "requestBody",
    "responseBody"
  ].some((key) => Object.hasOwn(value, key)));
}

function isStrictlyIncreasing(values: readonly number[]): boolean {
  return values.every((value, index) => index === 0 || value > values[index - 1]);
}
