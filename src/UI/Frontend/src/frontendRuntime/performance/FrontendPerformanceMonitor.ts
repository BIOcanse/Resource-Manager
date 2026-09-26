import type {
  RequestAttemptObservation
} from "../request/RequestClient.ts";
import type {
  SourceActivityObservation
} from "../source/SourceRegistry.ts";
import type { TaskRegistryChange } from "../task/TaskSnapshot.ts";

const schemaVersion = 1 as const;
const minimumCapacity = 256;
const maximumCapacity = 100_000;

interface FrontendPerformanceBootstrap {
  readonly schemaVersion: 1;
  readonly enabled: true;
  readonly capacity: number;
}

interface FrontendPerformanceEventBase {
  readonly sequence: number;
  readonly at: number;
}

export type FrontendPerformanceEvent =
  | (FrontendPerformanceEventBase & {
      readonly kind: "lifecycle";
      readonly phase: string;
    })
  | (FrontendPerformanceEventBase & {
      readonly kind: "request";
      readonly requestId: string;
      readonly requestSequence: number;
      readonly key: string;
      readonly decoderId: string;
      readonly method: string;
      readonly backendEpoch: string | null;
      readonly startedAt: number;
      readonly finishedAt: number;
      readonly durationMs: number;
      readonly outcome: string;
    })
  | (FrontendPerformanceEventBase & {
      readonly kind: "source";
      readonly phase: SourceActivityObservation["phase"];
      readonly sourceKey: string;
      readonly attempt: number;
      readonly generation: number;
      readonly trigger: string;
      readonly outcome: string | null;
      readonly startedAt: number;
      readonly finishedAt: number | null;
      readonly durationMs: number | null;
      readonly leaseCount: number;
      readonly activeLeaseCount: number;
      readonly inFlightCount: 0 | 1;
    })
  | (FrontendPerformanceEventBase & {
      readonly kind: "task";
      readonly changeKind: TaskRegistryChange["kind"];
      readonly taskId: string;
      readonly registryRevision: number;
      readonly key: string | null;
      readonly scopeKey: string | null;
      readonly status: string | null;
      readonly taskRevision: number | null;
      readonly createdAt: string | null;
      readonly startedAt: string | null;
      readonly completedAt: string | null;
    })
  | (FrontendPerformanceEventBase & {
      readonly kind: "route";
      readonly routeId: number;
      readonly page: string;
      readonly phase: "intent" | "commit" | "frame-1" | "frame-2";
      readonly intentAt: number | null;
      readonly elapsedFromIntentMs: number | null;
    })
  | (FrontendPerformanceEventBase & {
      readonly kind: "long-task";
      readonly name: string;
      readonly durationMs: number;
    });

type FrontendPerformanceEventInput<T = FrontendPerformanceEvent> =
  T extends FrontendPerformanceEvent
    ? Omit<T, "sequence">
    : never;

export interface FrontendPerformanceSnapshot {
  readonly schemaVersion: 1;
  readonly enabled: true;
  readonly capacity: number;
  readonly startedAt: number;
  readonly capturedAt: number;
  readonly droppedEventCount: number;
  readonly events: readonly FrontendPerformanceEvent[];
}

export interface FrontendPerformancePublicApi {
  snapshot(): FrontendPerformanceSnapshot;
  reset(): void;
}

export interface FrontendPerformanceMonitor {
  readonly enabled: boolean;
  readonly requestObserver: ((observation: RequestAttemptObservation) => void) | undefined;
  readonly sourceObserver: ((observation: SourceActivityObservation) => void) | undefined;
  recordTaskChange(change: TaskRegistryChange): void;
  markLifecycle(phase: string): void;
  markRouteIntent(page: string): void;
  markRouteCommit(page: string): void;
  snapshot(): FrontendPerformanceSnapshot | null;
  reset(): void;
  dispose(): void;
}

interface FrontendPerformanceMonitorOptions {
  readonly bootstrap?: unknown;
  readonly now?: () => number;
  readonly scheduleFrame?: (callback: FrameRequestCallback) => number;
  readonly createLongTaskObserver?: (
    callback: (entry: Pick<PerformanceEntry, "name" | "startTime" | "duration">) => void
  ) => (() => void) | null;
  readonly exposureTarget?: Record<string, unknown> | null;
}

interface PendingRoute {
  readonly routeId: number;
  readonly page: string;
  readonly intentAt: number;
}

export function createFrontendPerformanceMonitor(
  options: FrontendPerformanceMonitorOptions = {}
): FrontendPerformanceMonitor {
  const target = options.exposureTarget === undefined
    ? readDefaultExposureTarget()
    : options.exposureTarget;
  const bootstrap = normalizeBootstrap(
    options.bootstrap === undefined
      ? target?.__resourceManagerFrontendPerformanceBootstrap
      : options.bootstrap);
  if (!bootstrap) {
    return disabledMonitor;
  }

  const monitor = new EnabledFrontendPerformanceMonitor({
    bootstrap,
    now: options.now ?? defaultMonotonicNow,
    scheduleFrame: options.scheduleFrame ?? defaultScheduleFrame,
    createLongTaskObserver: options.createLongTaskObserver ?? createBrowserLongTaskObserver,
    exposureTarget: target
  });
  return monitor;
}

class EnabledFrontendPerformanceMonitor implements FrontendPerformanceMonitor {
  readonly enabled = true;
  readonly requestObserver = (observation: RequestAttemptObservation) => {
    const attempt = observation.attempt;
    this.add({
      kind: "request",
      at: attempt.finishedAt,
      requestId: attempt.id,
      requestSequence: attempt.sequence,
      key: normalizeLabel(attempt.key),
      decoderId: normalizeLabel(attempt.decoderId),
      method: normalizeLabel(attempt.method),
      backendEpoch: attempt.backendEpoch,
      startedAt: attempt.startedAt,
      finishedAt: attempt.finishedAt,
      durationMs: attempt.durationMs,
      outcome: observation.outcome
    });
  };
  readonly sourceObserver = (observation: SourceActivityObservation) => {
    this.add({
      kind: "source",
      at: observation.finishedAt ?? observation.startedAt,
      phase: observation.phase,
      sourceKey: normalizeLabel(observation.sourceKey),
      attempt: observation.attempt,
      generation: observation.generation,
      trigger: normalizeLabel(observation.trigger),
      outcome: observation.outcome,
      startedAt: observation.startedAt,
      finishedAt: observation.finishedAt,
      durationMs: observation.durationMs,
      leaseCount: observation.leaseCount,
      activeLeaseCount: observation.activeLeaseCount,
      inFlightCount: observation.inFlightCount
    });
  };

  private readonly capacity: number;
  private readonly now: () => number;
  private readonly scheduleFrame: (callback: FrameRequestCallback) => number;
  private readonly exposureTarget: Record<string, unknown> | null;
  private readonly publicApi: FrontendPerformancePublicApi;
  private readonly entries: Array<FrontendPerformanceEvent | undefined>;
  private readonly disconnectLongTaskObserver: (() => void) | null;
  private startedAt: number;
  private firstIndex = 0;
  private count = 0;
  private nextEventSequence = 0;
  private nextRouteId = 0;
  private droppedEventCount = 0;
  private pendingRoute: PendingRoute | null = null;
  private disposed = false;

  constructor(options: {
    readonly bootstrap: FrontendPerformanceBootstrap;
    readonly now: () => number;
    readonly scheduleFrame: (callback: FrameRequestCallback) => number;
    readonly createLongTaskObserver: FrontendPerformanceMonitorOptions["createLongTaskObserver"];
    readonly exposureTarget: Record<string, unknown> | null;
  }) {
    this.capacity = options.bootstrap.capacity;
    this.now = options.now;
    this.scheduleFrame = options.scheduleFrame;
    this.exposureTarget = options.exposureTarget;
    this.entries = new Array<FrontendPerformanceEvent | undefined>(this.capacity);
    this.startedAt = this.readNow();
    this.publicApi = Object.freeze({
      snapshot: () => this.readSnapshot(),
      reset: () => this.reset()
    });
    if (this.exposureTarget) {
      this.exposureTarget.__resourceManagerFrontendPerformance = this.publicApi;
    }
    this.disconnectLongTaskObserver = options.createLongTaskObserver?.((entry) => {
      if (entry.startTime < this.startedAt) {
        return;
      }
      this.add({
        kind: "long-task",
        at: entry.startTime,
        name: normalizeLabel(entry.name),
        durationMs: normalizeDuration(entry.duration)
      });
    }) ?? null;
    this.markLifecycle("runtime-created");
  }

  recordTaskChange(change: TaskRegistryChange): void {
    if (change.kind === "remove") {
      this.add({
        kind: "task",
        at: this.readNow(),
        changeKind: change.kind,
        taskId: normalizeLabel(change.taskId),
        registryRevision: change.revision,
        key: null,
        scopeKey: null,
        status: null,
        taskRevision: null,
        createdAt: null,
        startedAt: null,
        completedAt: null
      });
      return;
    }

    const snapshot = change.snapshot;
    this.add({
      kind: "task",
      at: this.readNow(),
      changeKind: change.kind,
      taskId: normalizeLabel(snapshot.id),
      registryRevision: change.revision,
      key: normalizeLabel(snapshot.descriptor.key),
      scopeKey: normalizeLabel(snapshot.descriptor.scopeKey),
      status: snapshot.status,
      taskRevision: snapshot.revision,
      createdAt: snapshot.createdAt,
      startedAt: snapshot.startedAt,
      completedAt: snapshot.completedAt
    });
  }

  markLifecycle(phase: string): void {
    this.add({
      kind: "lifecycle",
      at: this.readNow(),
      phase: normalizeLabel(phase)
    });
  }

  markRouteIntent(page: string): void {
    if (this.disposed) {
      return;
    }
    const at = this.readNow();
    const route: PendingRoute = {
      routeId: ++this.nextRouteId,
      page: normalizeLabel(page),
      intentAt: at
    };
    this.pendingRoute = route;
    this.addRoutePhase(route, "intent", at);
  }

  markRouteCommit(page: string): void {
    if (this.disposed) {
      return;
    }
    const normalizedPage = normalizeLabel(page);
    const route = this.pendingRoute?.page === normalizedPage
      ? this.pendingRoute
      : {
          routeId: ++this.nextRouteId,
          page: normalizedPage,
          intentAt: this.readNow()
        };
    this.pendingRoute = null;
    this.addRoutePhase(route, "commit", this.readNow());
    this.scheduleFrame((firstFrameAt) => {
      this.addRoutePhase(route, "frame-1", normalizeTimestamp(firstFrameAt, this.readNow()));
      this.scheduleFrame((secondFrameAt) => {
        this.addRoutePhase(route, "frame-2", normalizeTimestamp(secondFrameAt, this.readNow()));
      });
    });
  }

  snapshot(): FrontendPerformanceSnapshot {
    return this.readSnapshot();
  }

  reset(): void {
    if (this.disposed) {
      return;
    }
    this.entries.fill(undefined);
    this.firstIndex = 0;
    this.count = 0;
    this.droppedEventCount = 0;
    this.pendingRoute = null;
    this.startedAt = this.readNow();
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.markLifecycle("runtime-disposed");
    this.disposed = true;
    this.disconnectLongTaskObserver?.();
    if (this.exposureTarget?.__resourceManagerFrontendPerformance === this.publicApi) {
      delete this.exposureTarget.__resourceManagerFrontendPerformance;
    }
  }

  private addRoutePhase(
    route: PendingRoute,
    phase: "intent" | "commit" | "frame-1" | "frame-2",
    at: number
  ): void {
    this.add({
      kind: "route",
      at,
      routeId: route.routeId,
      page: route.page,
      phase,
      intentAt: route.intentAt,
      elapsedFromIntentMs: Math.max(0, at - route.intentAt)
    });
  }

  private add(
    event: FrontendPerformanceEventInput
  ): void {
    if (this.disposed) {
      return;
    }
    const entry = Object.freeze({
      ...event,
      sequence: ++this.nextEventSequence
    }) as FrontendPerformanceEvent;
    if (this.count < this.capacity) {
      const index = (this.firstIndex + this.count) % this.capacity;
      this.entries[index] = entry;
      this.count += 1;
      return;
    }
    this.entries[this.firstIndex] = entry;
    this.firstIndex = (this.firstIndex + 1) % this.capacity;
    this.droppedEventCount += 1;
  }

  private readSnapshot(): FrontendPerformanceSnapshot {
    const events: FrontendPerformanceEvent[] = [];
    for (let offset = 0; offset < this.count; offset += 1) {
      const entry = this.entries[(this.firstIndex + offset) % this.capacity];
      if (entry) {
        events.push(entry);
      }
    }
    return Object.freeze({
      schemaVersion,
      enabled: true,
      capacity: this.capacity,
      startedAt: this.startedAt,
      capturedAt: this.readNow(),
      droppedEventCount: this.droppedEventCount,
      events: Object.freeze(events)
    });
  }

  private readNow(): number {
    return normalizeTimestamp(this.now(), Date.now());
  }
}

const disabledMonitor: FrontendPerformanceMonitor = Object.freeze({
  enabled: false,
  requestObserver: undefined,
  sourceObserver: undefined,
  recordTaskChange: () => undefined,
  markLifecycle: () => undefined,
  markRouteIntent: () => undefined,
  markRouteCommit: () => undefined,
  snapshot: () => null,
  reset: () => undefined,
  dispose: () => undefined
});

function normalizeBootstrap(value: unknown): FrontendPerformanceBootstrap | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }
  const candidate = value as Record<string, unknown>;
  if (candidate.schemaVersion !== schemaVersion
    || candidate.enabled !== true
    || typeof candidate.capacity !== "number"
    || !Number.isSafeInteger(candidate.capacity)
    || candidate.capacity < minimumCapacity
    || candidate.capacity > maximumCapacity) {
    return null;
  }
  return Object.freeze({
    schemaVersion,
    enabled: true,
    capacity: candidate.capacity
  });
}

function readDefaultExposureTarget(): Record<string, unknown> | null {
  return typeof window === "undefined"
    ? null
    : window as unknown as Record<string, unknown>;
}

function defaultMonotonicNow(): number {
  return globalThis.performance?.now?.() ?? Date.now();
}

function defaultScheduleFrame(callback: FrameRequestCallback): number {
  if (typeof globalThis.requestAnimationFrame === "function") {
    return globalThis.requestAnimationFrame(callback);
  }
  return globalThis.setTimeout(
    () => callback(defaultMonotonicNow()),
    0) as unknown as number;
}

function createBrowserLongTaskObserver(
  callback: (entry: Pick<PerformanceEntry, "name" | "startTime" | "duration">) => void
): (() => void) | null {
  if (typeof globalThis.PerformanceObserver !== "function") {
    return null;
  }
  try {
    const observer = new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        callback(entry);
      }
    });
    observer.observe({ type: "longtask", buffered: true });
    return () => observer.disconnect();
  } catch {
    return null;
  }
}

function normalizeTimestamp(value: number, fallback: number): number {
  return Number.isFinite(value) && value >= 0 ? value : fallback;
}

function normalizeDuration(value: number): number {
  return Number.isFinite(value) && value >= 0 ? value : 0;
}

function normalizeLabel(value: string): string {
  return String(value ?? "").slice(0, 160);
}
