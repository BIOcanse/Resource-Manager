import { RequestProblem } from "../request/RequestProblem.ts";
import type { BackendSessionSnapshot } from "../session/backendSessionTypes.ts";
import type {
  CanonicalSourceQuery,
  SourceDemand,
  SourceDescriptor,
  SourceFamily,
  SourceFamilyDescriptor,
  SourceHandle,
  SourceLease,
  SourceRetryPolicy
} from "./SourceDescriptor.ts";
import type {
  SourceDomainRevision,
  SourceSnapshot,
  SourceStatus
} from "./SourceSnapshot.ts";

type SourceListener = (snapshot: SourceSnapshot<unknown>) => void;

interface BackendSessionSource {
  readonly snapshot: BackendSessionSnapshot;
  subscribe(listener: (snapshot: BackendSessionSnapshot) => void): () => void;
  retry?(signal?: AbortSignal): Promise<unknown>;
}

export type SourceRefreshTrigger =
  | "manual"
  | "lease-activated"
  | "demand-activated"
  | "scheduled"
  | "backend-ready"
  | "authoritative";

export type SourceActivityOutcome =
  | "ready"
  | "stale"
  | "error"
  | "rejected-domain-revision"
  | "cancelled"
  | "accepted-authoritative";

export interface SourceActivityObservation {
  readonly phase: "started" | "settled" | "cancelled" | "authoritative";
  readonly sourceKey: string;
  readonly attempt: number;
  readonly generation: number;
  readonly trigger: SourceRefreshTrigger;
  readonly outcome: SourceActivityOutcome | null;
  readonly startedAt: number;
  readonly finishedAt: number | null;
  readonly durationMs: number | null;
  readonly leaseCount: number;
  readonly activeLeaseCount: number;
  readonly inFlightCount: 0 | 1;
}

export interface SourceRegistryOptions {
  readonly schedule?: (callback: () => void, delayMs: number) => unknown;
  readonly cancelSchedule?: (handle: unknown) => void;
  readonly recordRetentionMs?: number;
  readonly random?: () => number;
  readonly onListenerError?: (error: unknown, sourceKey: string) => void;
  readonly monotonicNow?: () => number;
  readonly onActivity?: (observation: SourceActivityObservation) => void;
}

interface LeaseState {
  demand: SourceDemand;
  readonly listeners: Set<SourceListener>;
}

interface InFlightState {
  readonly token: number;
  readonly attempt: number;
  readonly trigger: SourceRefreshTrigger;
  readonly startedAt: number;
  readonly controller: AbortController;
  readonly promise: Promise<SourceSnapshot<unknown>>;
}

interface SourceRecord {
  readonly registryKey: string;
  readonly key: string;
  readonly registration: object;
  readonly descriptor: SourceDescriptor<unknown>;
  readonly leases: Map<number, LeaseState>;
  snapshot: SourceSnapshot<unknown>;
  backendEpoch: string | null;
  lastGood: SourceSnapshot<unknown> | null;
  nextAttempt: number;
  generation: number;
  inFlight: InFlightState | null;
  consecutiveFailures: number;
  automaticRefreshBlocked: boolean;
  refreshTimer: unknown | null;
  cleanupTimer: unknown | null;
}

const defaultRecordRetentionMs = 30_000;

export class SourceRegistry {
  private readonly records = new Map<string, SourceRecord>();
  private readonly backendSession: BackendSessionSource;
  private readonly schedule: (callback: () => void, delayMs: number) => unknown;
  private readonly cancelSchedule: (handle: unknown) => void;
  private readonly recordRetentionMs: number;
  private readonly random: () => number;
  private readonly onListenerError: (error: unknown, sourceKey: string) => void;
  private readonly monotonicNow: () => number;
  private readonly onActivity: ((observation: SourceActivityObservation) => void) | undefined;
  private readonly unsubscribeBackendSession: () => void;
  private nextLeaseId = 0;
  private disposed = false;

  constructor(
    backendSession: BackendSessionSource,
    options: SourceRegistryOptions = {})
  {
    this.backendSession = backendSession;
    this.schedule = options.schedule
      ?? ((callback, delayMs) => globalThis.setTimeout(callback, delayMs));
    this.cancelSchedule = options.cancelSchedule
      ?? ((handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>));
    this.recordRetentionMs = normalizeRetention(options.recordRetentionMs);
    this.random = options.random ?? Math.random;
    this.onListenerError = options.onListenerError
      ?? ((error, sourceKey) => console.error(
        `Source '${sourceKey}' listener failed.`,
        error));
    this.monotonicNow = options.monotonicNow ?? defaultMonotonicNow;
    this.onActivity = options.onActivity;
    this.unsubscribeBackendSession = backendSession.subscribe(
      (snapshot) => this.handleBackendSession(snapshot));
  }

  get recordCount(): number {
    return this.records.size;
  }

  define<T>(descriptor: SourceDescriptor<T>): SourceHandle<T> {
    this.throwIfDisposed();
    const key = normalizeKey(descriptor.key);
    const retryPolicy = normalizeRetryPolicy(descriptor.retryPolicy);
    const normalizedDescriptor = Object.freeze({
      ...descriptor,
      key,
      retryPolicy
    });

    return Object.freeze({
      key,
      acquire: (initialDemand: SourceDemand = { active: true }) =>
        this.acquire(
          normalizedDescriptor,
          initialDemand,
          staticRegistryKey(key),
          normalizedDescriptor)
    });
  }

  defineFamily<TQuery, TValue>(
    descriptor: SourceFamilyDescriptor<TQuery, TValue>
  ): SourceFamily<TQuery, TValue> {
    this.throwIfDisposed();
    const familyKey = normalizeKey(descriptor.key);
    const normalizedDescriptor = Object.freeze({
      ...descriptor,
      key: familyKey,
      retryPolicy: normalizeRetryPolicy(descriptor.retryPolicy)
    });
    const registration = normalizedDescriptor as object;

    return Object.freeze({
      key: familyKey,
      acquire: (
        query: TQuery,
        initialDemand: SourceDemand = { active: true }
      ) => {
        const canonical = normalizedDescriptor.canonicalizeQuery
          ? normalizeCanonicalQuery(normalizedDescriptor.canonicalizeQuery(query))
          : canonicalizeSourceQuery(query);
        const queryKey = canonical.key;
        const sourceKey = parameterizedSourceKey(familyKey, queryKey);
        const instanceDescriptor: SourceDescriptor<TValue> = Object.freeze({
          key: sourceKey,
          load: (signal: AbortSignal) => normalizedDescriptor.load(canonical.value, signal),
          retryPolicy: normalizedDescriptor.retryPolicy,
          mergeValue: normalizedDescriptor.mergeValue,
          selectDomainRevision: normalizedDescriptor.selectDomainRevision,
          selectCapturedAt: normalizedDescriptor.selectCapturedAt,
          retainLastGood: normalizedDescriptor.retainLastGood,
          canRetainStale: normalizedDescriptor.canRetainStale
        });
        return this.acquire(
          instanceDescriptor,
          initialDemand,
          familyRegistryKey(familyKey, queryKey),
          registration);
      }
    });
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    this.unsubscribeBackendSession();
    for (const record of this.records.values()) {
      this.clearRefreshTimer(record);
      this.clearCleanupTimer(record);
      this.cancelAttempt(record, "SourceRegistry disposed.");
      this.publish(record, {
        status: "disposed",
        data: null,
        error: new Error("SourceRegistry disposed."),
        backendEpoch: null,
        acceptedAttempt: null,
        domainRevision: null,
        capturedAt: null
      });
      record.leases.clear();
    }
    this.records.clear();
  }

  private acquire<T>(
    descriptor: SourceDescriptor<T>,
    initialDemand: SourceDemand,
    registryKey: string,
    registration: object
  ): SourceLease<T> {
    this.throwIfDisposed();
    const untypedDescriptor = descriptor as unknown as SourceDescriptor<unknown>;
    let record = this.records.get(registryKey);
    if (record && record.registration !== registration) {
      throw new Error(`Source '${descriptor.key}' already has a different descriptor.`);
    }
    if (!record) {
      record = this.createRecord(registryKey, registration, untypedDescriptor);
      this.records.set(record.registryKey, record);
    }
    this.clearCleanupTimer(record);

    const leaseId = ++this.nextLeaseId;
    const leaseState: LeaseState = {
      demand: normalizeDemand(initialDemand),
      listeners: new Set<SourceListener>()
    };
    record.leases.set(leaseId, leaseState);
    let released = false;

    const lease: SourceLease<T> = {
      key: descriptor.key,
      get snapshot() {
        return record!.snapshot as SourceSnapshot<T>;
      },
      subscribe: (listener) => {
        if (released) {
          throw new Error(`Source lease '${descriptor.key}' is released.`);
        }
        const untypedListener = listener as unknown as SourceListener;
        leaseState.listeners.add(untypedListener);
        return () => leaseState.listeners.delete(untypedListener);
      },
      setDemand: (demand) => {
        if (released) {
          throw new Error(`Source lease '${descriptor.key}' is released.`);
        }
        const normalizedDemand = normalizeDemand(demand);
        if (demandsEqual(leaseState.demand, normalizedDemand)) {
          return;
        }
        const wasActive = this.hasActiveDemand(record!);
        leaseState.demand = normalizedDemand;
        this.reconcileDemand(record!, wasActive);
      },
      refresh: () => {
        if (released) {
          return Promise.reject(
            new Error(`Source lease '${descriptor.key}' is released.`));
        }
        return this.startRefresh(record!, "manual") as Promise<SourceSnapshot<T>>;
      },
      acceptAuthoritative: (value) => {
        if (released) {
          throw new Error(`Source lease '${descriptor.key}' is released.`);
        }
        return this.acceptAuthoritative(record!, value) as SourceSnapshot<T>;
      },
      release: () => {
        if (released) {
          return;
        }
        released = true;
        const wasActive = this.hasActiveDemand(record!);
        leaseState.listeners.clear();
        record!.leases.delete(leaseId);
        this.reconcileDemand(record!, wasActive);
        if (record!.leases.size === 0) {
          this.scheduleCleanup(record!);
        }
      }
    };

    if (leaseState.demand.active) {
      void this.startRefresh(record, "lease-activated");
    }
    return lease;
  }

  private createRecord(
    registryKey: string,
    registration: object,
    descriptor: SourceDescriptor<unknown>
  ): SourceRecord {
    const session = this.backendSession.snapshot;
    const backendEpoch = session.status === "ready"
      ? session.session?.epoch ?? null
      : null;
    const status: SourceStatus = session.status === "unavailable"
      ? "unavailable"
      : session.status === "disposed"
        ? "disposed"
        : "idle";
    return {
      registryKey,
      key: descriptor.key,
      registration,
      descriptor,
      leases: new Map<number, LeaseState>(),
      snapshot: Object.freeze({
        key: descriptor.key,
        status,
        data: null,
        error: session.status === "unavailable" || session.status === "disposed"
          ? new Error(session.reason ?? "Backend session unavailable.")
          : null,
        backendEpoch,
        revision: 0,
        acceptedAttempt: null,
        domainRevision: null,
        capturedAt: null
      }),
      backendEpoch,
      lastGood: null,
      nextAttempt: 0,
      generation: 0,
      inFlight: null,
      consecutiveFailures: 0,
      automaticRefreshBlocked: false,
      refreshTimer: null,
      cleanupTimer: null
    };
  }

  private reconcileDemand(record: SourceRecord, wasActive: boolean): void {
    const active = this.hasActiveDemand(record);
    if (!active) {
      this.clearRefreshTimer(record);
      const hadAttempt = record.inFlight !== null;
      this.cancelAttempt(record, "Source has no active demand.");
      if (!this.retainsLastGood(record)) {
        this.publishIdle(record);
      } else if (hadAttempt) {
        this.restoreSettledSnapshot(record);
      }
      return;
    }
    if (!wasActive) {
      void this.startRefresh(record, "demand-activated");
      return;
    }
    this.scheduleNextRefresh(record);
  }

  private startRefresh(
    record: SourceRecord,
    reason: Exclude<SourceRefreshTrigger, "authoritative">
  ): Promise<SourceSnapshot<unknown>> {
    if (this.disposed) {
      return Promise.resolve(record.snapshot);
    }
    if (record.inFlight) {
      return record.inFlight.promise;
    }

    const session = this.backendSession.snapshot;
    if (session.status !== "ready" || !session.session) {
      this.publish(record, {
        status: session.status === "unavailable" ? "unavailable" : "loading",
        data: null,
        error: session.status === "unavailable"
          ? new Error(session.reason ?? "Backend session unavailable.")
          : null,
        backendEpoch: null,
        acceptedAttempt: null,
        domainRevision: null,
        capturedAt: null
      });
      if (reason === "manual"
        && session.status !== "disposed"
        && this.backendSession.retry) {
        return this.backendSession.retry()
          .then(() => this.startRefresh(record, "backend-ready"))
          .catch(() => record.snapshot);
      }
      return Promise.resolve(record.snapshot);
    }

    if (record.backendEpoch !== session.session.epoch) {
      this.resetForEpoch(record, session.session.epoch);
    }

    this.clearRefreshTimer(record);
    const controller = new AbortController();
    const token = ++record.generation;
    const attempt = ++record.nextAttempt;
    const startedAt = this.onActivity ? this.readMonotonicNow() : 0;
    const previous = this.readAcceptedSnapshot(record);
    this.publish(record, {
      status: previous ? "refreshing" : "loading",
      data: previous?.data ?? null,
      error: null,
      backendEpoch: record.backendEpoch,
      acceptedAttempt: previous?.acceptedAttempt ?? null,
      domainRevision: previous?.domainRevision ?? null,
      capturedAt: previous?.capturedAt ?? null
    });

    let flight!: InFlightState;
    const promise = Promise.resolve()
      .then(() => record.descriptor.load(controller.signal))
      .then((incoming) => {
        if (!this.canCommit(record, token)) {
          return record.snapshot;
        }
        const domainRevision = record.descriptor.selectDomainRevision?.(incoming) ?? null;
        const previousRevision = previous?.domainRevision ?? null;
        if (domainRevision !== null
          && previousRevision !== null
          && compareDomainRevision(domainRevision, previousRevision) < 0) {
          this.restoreSettledSnapshot(record);
          this.observeSourceActivity(
            record,
            flight,
            "settled",
            "rejected-domain-revision",
            0);
          return record.snapshot;
        }
        const previousValue = previous?.data ?? null;
        const value = record.descriptor.mergeValue
          ? record.descriptor.mergeValue(previousValue, incoming)
          : incoming;
        const capturedAt = normalizeCapturedAt(
          record.descriptor.selectCapturedAt?.(value) ?? null);
        this.publish(record, {
          status: "ready",
          data: value,
          error: null,
          backendEpoch: record.backendEpoch,
          acceptedAttempt: attempt,
          domainRevision,
          capturedAt
        });
        this.resetFailureState(record);
        if (this.retainsLastGood(record)) {
          record.lastGood = record.snapshot;
        }
        this.observeSourceActivity(record, flight, "settled", "ready", 0);
        return record.snapshot;
      })
      .catch((error: unknown) => {
        if (!this.canCommit(record, token)) {
          return record.snapshot;
        }
        this.recordFailure(record, error);
        const stale = this.retainsLastGood(record)
          && this.canRetainStale(record, error)
          ? this.readAcceptedSnapshot(record)
          : null;
        this.publish(record, {
          status: stale ? "stale" : "error",
          data: stale?.data ?? null,
          error,
          backendEpoch: record.backendEpoch,
          acceptedAttempt: stale?.acceptedAttempt ?? null,
          domainRevision: stale?.domainRevision ?? null,
          capturedAt: stale?.capturedAt ?? null
        });
        this.observeSourceActivity(
          record,
          flight,
          "settled",
          stale ? "stale" : "error",
          0);
        return record.snapshot;
      })
      .finally(() => {
        if (record.inFlight === flight) {
          record.inFlight = null;
          this.scheduleNextRefresh(record);
          if (record.leases.size === 0) {
            this.scheduleCleanup(record);
          }
        }
      });
    flight = { token, attempt, trigger: reason, startedAt, controller, promise };
    record.inFlight = flight;
    this.observeSourceActivity(record, flight, "started", null, 1);
    return promise;
  }

  private acceptAuthoritative<T>(
    record: SourceRecord,
    incoming: T
  ): SourceSnapshot<T> {
    this.throwIfDisposed();
    const session = this.backendSession.snapshot;
    if (session.status !== "ready" || !session.session) {
      throw new Error(
        `Source '${record.key}' cannot accept a value without a ready backend session.`);
    }
    if (record.backendEpoch !== session.session.epoch) {
      this.cancelAttempt(record, "Backend epoch changed before authoritative acceptance.");
      this.resetForEpoch(record, session.session.epoch);
    }

    const domainRevision = record.descriptor.selectDomainRevision?.(incoming) ?? null;
    const previous = this.readAcceptedSnapshot(record);
    const previousRevision = previous?.domainRevision ?? null;
    if (domainRevision !== null
      && previousRevision !== null
      && compareDomainRevision(domainRevision, previousRevision) < 0) {
      return record.snapshot as SourceSnapshot<T>;
    }
    const previousValue = previous?.data ?? null;
    const value = record.descriptor.mergeValue
      ? record.descriptor.mergeValue(previousValue, incoming)
      : incoming;
    const capturedAt = normalizeCapturedAt(
      record.descriptor.selectCapturedAt?.(value) ?? null);

    this.clearRefreshTimer(record);
    this.cancelAttempt(record, "Source superseded by an authoritative command result.");
    const acceptedAttempt = ++record.nextAttempt;
    this.publish(record, {
      status: "ready",
      data: value,
      error: null,
      backendEpoch: record.backendEpoch,
      acceptedAttempt,
      domainRevision,
      capturedAt
    });
    this.resetFailureState(record);
    if (this.retainsLastGood(record)) {
      record.lastGood = record.snapshot;
    }
    if (this.onActivity) {
      const acceptedAt = this.readMonotonicNow();
      this.observeSourceActivity(record, {
        token: record.generation,
        attempt: acceptedAttempt,
        trigger: "authoritative",
        startedAt: acceptedAt
      }, "authoritative", "accepted-authoritative", 0, acceptedAt);
    }
    this.scheduleNextRefresh(record);
    return record.snapshot as SourceSnapshot<T>;
  }

  private handleBackendSession(snapshot: BackendSessionSnapshot): void {
    if (this.disposed) {
      return;
    }
    for (const record of this.records.values()) {
      if (snapshot.status === "ready" && snapshot.session) {
        if (record.backendEpoch !== snapshot.session.epoch) {
          this.cancelAttempt(record, "Backend epoch changed.");
          this.resetForEpoch(record, snapshot.session.epoch);
          if (this.hasActiveDemand(record)) {
            void this.startRefresh(record, "backend-ready");
          }
        }
        continue;
      }

      this.cancelAttempt(record, "Backend session unavailable.");
      record.backendEpoch = null;
      record.lastGood = null;
      this.resetFailureState(record);
      this.publish(record, {
        status: snapshot.status === "disposed" ? "disposed"
          : snapshot.status === "unavailable" ? "unavailable" : "loading",
        data: null,
        error: snapshot.status === "waiting"
          ? null
          : new Error(snapshot.reason ?? "Backend session unavailable."),
        backendEpoch: null,
        acceptedAttempt: null,
        domainRevision: null,
        capturedAt: null
      });
    }
  }

  private resetForEpoch(record: SourceRecord, epoch: string): void {
    record.backendEpoch = epoch;
    record.lastGood = null;
    this.resetFailureState(record);
    this.publish(record, {
      status: this.hasActiveDemand(record) ? "loading" : "idle",
      data: null,
      error: null,
      backendEpoch: epoch,
      acceptedAttempt: null,
      domainRevision: null,
      capturedAt: null
    });
  }

  private restoreSettledSnapshot(record: SourceRecord): void {
    if (!this.retainsLastGood(record) && !this.hasActiveDemand(record)) {
      this.publishIdle(record);
      return;
    }
    const retained = this.readAcceptedSnapshot(record);
    if (retained) {
      if (record.snapshot === retained
        && record.snapshot.status === "ready") {
        return;
      }
      this.publish(record, {
        status: "ready",
        data: retained.data,
        error: null,
        backendEpoch: retained.backendEpoch,
        acceptedAttempt: retained.acceptedAttempt,
        domainRevision: retained.domainRevision,
        capturedAt: retained.capturedAt
      });
      return;
    }

    const session = this.backendSession.snapshot;
    if (session.status !== "ready" || session.session?.epoch !== record.backendEpoch) {
      return;
    }
    if (record.snapshot.status === "idle"
      && record.snapshot.backendEpoch === record.backendEpoch) {
      return;
    }
    this.publishIdle(record);
  }

  private publishIdle(record: SourceRecord): void {
    this.publish(record, {
      status: "idle",
      data: null,
      error: null,
      backendEpoch: record.backendEpoch,
      acceptedAttempt: null,
      domainRevision: null,
      capturedAt: null
    });
  }

  private readAcceptedSnapshot(record: SourceRecord): SourceSnapshot<unknown> | null {
    if (this.retainsLastGood(record)) {
      return record.lastGood?.backendEpoch === record.backendEpoch
        ? record.lastGood
        : null;
    }
    const current = record.snapshot;
    return current.backendEpoch === record.backendEpoch
      && current.data !== null
      && (current.status === "ready" || current.status === "refreshing")
      ? current
      : null;
  }

  private retainsLastGood(record: SourceRecord): boolean {
    return record.descriptor.retainLastGood === true;
  }

  private publish(
    record: SourceRecord,
    update: Omit<SourceSnapshot<unknown>, "key" | "revision">
  ): void {
    record.snapshot = Object.freeze({
      key: record.key,
      ...update,
      revision: record.snapshot.revision + 1
    });
    for (const lease of record.leases.values()) {
      for (const listener of lease.listeners) {
        try {
          listener(record.snapshot);
        } catch (error: unknown) {
          try {
            this.onListenerError(error, record.key);
          } catch {
            // Diagnostic handlers cannot take ownership of source publication.
          }
        }
      }
    }
  }

  private scheduleNextRefresh(record: SourceRecord): void {
    this.clearRefreshTimer(record);
    if (this.disposed || record.inFlight || !this.hasActiveDemand(record)) {
      return;
    }
    const delay = this.resolveNextRefreshDelay(record);
    if (delay === null) {
      return;
    }
    record.refreshTimer = this.schedule(() => {
      record.refreshTimer = null;
      void this.startRefresh(record, "scheduled");
    }, delay);
  }

  private scheduleCleanup(record: SourceRecord): void {
    if (this.disposed || record.leases.size > 0 || record.inFlight || record.cleanupTimer) {
      return;
    }
    record.cleanupTimer = this.schedule(() => {
      record.cleanupTimer = null;
      if (record.leases.size === 0
        && !record.inFlight
        && this.records.get(record.registryKey) === record) {
        this.clearRefreshTimer(record);
        this.records.delete(record.registryKey);
      }
    }, this.recordRetentionMs);
  }

  private cancelAttempt(record: SourceRecord, reason: string): void {
    const flight = record.inFlight;
    if (!flight) {
      return;
    }
    record.generation += 1;
    record.inFlight = null;
    if (!flight.controller.signal.aborted) {
      flight.controller.abort(new Error(reason));
    }
    this.observeSourceActivity(record, flight, "cancelled", "cancelled", 0);
  }

  private observeSourceActivity(
    record: SourceRecord,
    flight: Pick<InFlightState, "token" | "attempt" | "trigger" | "startedAt">,
    phase: SourceActivityObservation["phase"],
    outcome: SourceActivityOutcome | null,
    inFlightCount: 0 | 1,
    fixedFinishedAt?: number
  ): void {
    if (!this.onActivity) {
      return;
    }
    const finishedAt = phase === "started"
      ? null
      : fixedFinishedAt ?? this.readMonotonicNow();
    let activeLeaseCount = 0;
    for (const lease of record.leases.values()) {
      if (lease.demand.active) {
        activeLeaseCount += 1;
      }
    }
    try {
      this.onActivity(Object.freeze({
        phase,
        sourceKey: record.key,
        attempt: flight.attempt,
        generation: flight.token,
        trigger: flight.trigger,
        outcome,
        startedAt: flight.startedAt,
        finishedAt,
        durationMs: finishedAt === null
          ? null
          : Math.max(0, finishedAt - flight.startedAt),
        leaseCount: record.leases.size,
        activeLeaseCount,
        inFlightCount
      }));
    } catch {
      // Diagnostics cannot take ownership of source publication.
    }
  }

  private readMonotonicNow(): number {
    const value = this.monotonicNow();
    return Number.isFinite(value) && value >= 0 ? value : Date.now();
  }

  private canCommit(record: SourceRecord, token: number): boolean {
    const session = this.backendSession.snapshot;
    return !this.disposed
      && record.generation === token
      && session.status === "ready"
      && session.session?.epoch === record.backendEpoch;
  }

  private canRetainStale(record: SourceRecord, error: unknown): boolean {
    if (!this.retainsLastGood(record)) {
      return false;
    }
    if (record.descriptor.canRetainStale) {
      return record.descriptor.canRetainStale(error);
    }
    return !(error instanceof RequestProblem) || error.retryable;
  }

  private recordFailure(record: SourceRecord, error: unknown): void {
    record.consecutiveFailures += 1;
    record.automaticRefreshBlocked = error instanceof RequestProblem
      && !error.retryable;
  }

  private resetFailureState(record: SourceRecord): void {
    record.consecutiveFailures = 0;
    record.automaticRefreshBlocked = false;
  }

  private hasActiveDemand(record: SourceRecord): boolean {
    for (const lease of record.leases.values()) {
      if (lease.demand.active) {
        return true;
      }
    }
    return false;
  }

  private resolveNextRefreshDelay(record: SourceRecord): number | null {
    if (record.automaticRefreshBlocked) {
      return null;
    }
    let interval: number | null = null;
    for (const lease of record.leases.values()) {
      if (!lease.demand.active) {
        continue;
      }
      const candidate = normalizeInterval(lease.demand.refreshIntervalMs);
      if (candidate !== null && (interval === null || candidate < interval)) {
        interval = candidate;
      }
    }
    if (record.consecutiveFailures <= 0 || !record.descriptor.retryPolicy) {
      return interval;
    }

    const retryDelay = retryDelayForFailure(
      record.descriptor.retryPolicy,
      record.consecutiveFailures,
      this.random);
    return interval === null ? retryDelay : Math.max(interval, retryDelay);
  }

  private clearRefreshTimer(record: SourceRecord): void {
    if (record.refreshTimer === null) {
      return;
    }
    this.cancelSchedule(record.refreshTimer);
    record.refreshTimer = null;
  }

  private clearCleanupTimer(record: SourceRecord): void {
    if (record.cleanupTimer === null) {
      return;
    }
    this.cancelSchedule(record.cleanupTimer);
    record.cleanupTimer = null;
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error("SourceRegistry is disposed.");
    }
  }
}

export function canonicalizeSourceQuery<TQuery>(
  query: TQuery
): CanonicalSourceQuery<TQuery> {
  const value = cloneCanonicalQueryValue(query, new WeakSet<object>()) as TQuery;
  return Object.freeze({
    key: JSON.stringify(value),
    value
  });
}

function normalizeKey(value: string): string {
  const key = value.trim();
  if (!key) {
    throw new Error("A source key is required.");
  }
  return key;
}

function normalizeCanonicalQuery<TQuery>(
  query: CanonicalSourceQuery<TQuery>
): CanonicalSourceQuery<TQuery> {
  const key = normalizeQueryKey(query.key);
  const value = cloneCanonicalQueryValue(
    query.value,
    new WeakSet<object>()) as TQuery;
  return Object.freeze({ key, value });
}

function normalizeQueryKey(value: string): string {
  const key = value.trim();
  if (!key) {
    throw new Error("A canonical source query key is required.");
  }
  return key;
}

function staticRegistryKey(sourceKey: string): string {
  return `source\u0000${sourceKey}`;
}

function familyRegistryKey(familyKey: string, queryKey: string): string {
  return `family\u0000${familyKey}\u0000${queryKey}`;
}

function parameterizedSourceKey(familyKey: string, queryKey: string): string {
  return `${familyKey}?query=${encodeURIComponent(queryKey)}`;
}

function cloneCanonicalQueryValue(value: unknown, seen: WeakSet<object>): unknown {
  if (value === null || typeof value === "string" || typeof value === "boolean") {
    return value;
  }
  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      throw new Error("Source query numbers must be finite.");
    }
    return Object.is(value, -0) ? 0 : value;
  }
  if (typeof value !== "object") {
    throw new Error(`Unsupported source query value: ${typeof value}.`);
  }
  if (seen.has(value)) {
    throw new Error("Source queries cannot contain cycles.");
  }
  seen.add(value);
  try {
    if (Array.isArray(value)) {
      return Object.freeze(value.map((item) => cloneCanonicalQueryValue(item, seen)));
    }
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null) {
      throw new Error("Source query objects must be plain objects.");
    }
    if (Object.getOwnPropertySymbols(value).length > 0) {
      throw new Error("Source query objects cannot contain symbol keys.");
    }
    const result: Record<string, unknown> = {};
    for (const key of Object.keys(value).sort()) {
      result[key] = cloneCanonicalQueryValue(
        (value as Record<string, unknown>)[key],
        seen);
    }
    return Object.freeze(result);
  } finally {
    seen.delete(value);
  }
}

function normalizeDemand(demand: SourceDemand): SourceDemand {
  return Object.freeze({
    active: demand.active,
    refreshIntervalMs: normalizeInterval(demand.refreshIntervalMs)
  });
}

function demandsEqual(left: SourceDemand, right: SourceDemand): boolean {
  return left.active === right.active
    && normalizeInterval(left.refreshIntervalMs) === normalizeInterval(right.refreshIntervalMs);
}

function normalizeRetryPolicy(
  policy: SourceRetryPolicy | undefined
): SourceRetryPolicy | undefined {
  if (!policy) {
    return undefined;
  }
  const initialDelayMs = normalizePositiveDelay(
    policy.initialDelayMs,
    "initialDelayMs");
  const maximumDelayMs = normalizePositiveDelay(
    policy.maximumDelayMs,
    "maximumDelayMs");
  if (maximumDelayMs < initialDelayMs) {
    throw new Error("Source retry maximumDelayMs cannot be smaller than initialDelayMs.");
  }
  const multiplier = policy.multiplier ?? 2;
  if (!Number.isFinite(multiplier) || multiplier < 1) {
    throw new Error("Source retry multiplier must be a finite number greater than or equal to 1.");
  }
  const jitterRatio = policy.jitterRatio ?? 0;
  if (!Number.isFinite(jitterRatio) || jitterRatio < 0 || jitterRatio > 1) {
    throw new Error("Source retry jitterRatio must be between 0 and 1.");
  }
  return Object.freeze({
    initialDelayMs,
    maximumDelayMs,
    multiplier,
    jitterRatio
  });
}

function normalizePositiveDelay(value: number, field: string): number {
  if (!Number.isFinite(value) || value <= 0) {
    throw new Error(`Source retry ${field} must be a positive finite number.`);
  }
  return Math.max(1, Math.floor(value));
}

function retryDelayForFailure(
  policy: SourceRetryPolicy,
  consecutiveFailures: number,
  random: () => number
): number {
  const exponent = Math.max(0, consecutiveFailures - 1);
  const baseDelay = Math.min(
    policy.maximumDelayMs,
    policy.initialDelayMs * Math.pow(policy.multiplier ?? 2, exponent));
  const jitterRatio = policy.jitterRatio ?? 0;
  const randomUnit = Math.min(1, Math.max(0, random()));
  return Math.max(1, Math.floor(
    baseDelay + (baseDelay * jitterRatio * randomUnit)));
}

function normalizeInterval(value: number | null | undefined): number | null {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? Math.max(1, Math.floor(value))
    : null;
}

function normalizeRetention(value: number | undefined): number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0
    ? Math.floor(value)
    : defaultRecordRetentionMs;
}

function defaultMonotonicNow(): number {
  return globalThis.performance?.now?.() ?? Date.now();
}

function normalizeCapturedAt(value: string | null): string | null {
  return value && Number.isFinite(Date.parse(value)) ? value : null;
}

function compareDomainRevision(
  left: SourceDomainRevision,
  right: SourceDomainRevision
): number {
  if (typeof left === "number" && typeof right === "number") {
    return left === right ? 0 : left > right ? 1 : -1;
  }
  const leftText = String(left);
  const rightText = String(right);
  if (/^(0|[1-9][0-9]*)$/.test(leftText)
    && /^(0|[1-9][0-9]*)$/.test(rightText)) {
    if (leftText.length !== rightText.length) {
      return leftText.length > rightText.length ? 1 : -1;
    }
  }
  return leftText === rightText ? 0 : leftText > rightText ? 1 : -1;
}
