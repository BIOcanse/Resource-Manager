import type {
  ScopedTaskDescriptor,
  TaskConcurrency,
  TaskDescriptor,
  TaskDurability,
  TaskKey,
  TaskVisibility
} from "./TaskDescriptor.ts";
import type {
  TaskExecutionContext,
  TaskExecutor,
  TaskRun,
  TaskScope
} from "./TaskScope.ts";
import {
  isTerminalTaskStatus,
  type TaskOutcome,
  type TaskProgress,
  type TaskRegistryChange,
  type TaskSnapshot
} from "./TaskSnapshot.ts";

export interface TaskRegistryOptions {
  readonly now?: () => number;
  readonly schedule?: (callback: () => void, delayMs: number) => unknown;
  readonly cancelSchedule?: (handle: unknown) => void;
  readonly terminalRetentionMs?: number;
  readonly maxTerminalEntries?: number;
  readonly onListenerError?: (error: unknown, taskId: string) => void;
}

type TaskListener = (snapshot: TaskSnapshot) => void;
type TaskRegistryListener = (change: TaskRegistryChange) => void;

interface ScopeRecord {
  readonly key: string;
  readonly generation: number;
  open: boolean;
}

interface SlotContract {
  readonly token: object;
  readonly signature: string;
}

interface TaskRecord {
  readonly id: string;
  readonly sequence: number;
  readonly generation: number;
  readonly scope: ScopeRecord;
  readonly descriptor: TaskDescriptor;
  readonly slotKey: string;
  readonly serialGroupKey: string | null;
  readonly controller: AbortController;
  readonly executor: TaskExecutor<unknown>;
  readonly listeners: Set<TaskListener>;
  readonly completion: Promise<TaskOutcome<unknown>>;
  readonly resolveCompletion: (outcome: TaskOutcome<unknown>) => void;
  handle: TaskRun<unknown>;
  snapshot: TaskSnapshot;
  invalidated: boolean;
  executionStarted: boolean;
  executionSettled: boolean;
  terminalAtMs: number | null;
}

interface SerialGroup {
  readonly key: string;
  readonly queue: TaskRecord[];
  running: TaskRecord | null;
  drainScheduled: boolean;
}

interface NormalizedTaskDescriptor<T> {
  readonly keyToken: TaskKey<T>;
  readonly descriptor: TaskDescriptor;
}

type TaskPublication =
  | {
      readonly kind: "upsert";
      readonly record: TaskRecord;
      readonly snapshot: TaskSnapshot;
      readonly change: TaskRegistryChange;
    }
  | {
      readonly kind: "remove";
      readonly taskId: string;
      readonly change: TaskRegistryChange;
    };

const taskDurabilities = new Set<TaskDurability>([
  "transient",
  "frontend-session",
  "external-durable"
]);
const taskVisibilities = new Set<TaskVisibility>([
  "hidden",
  "diagnostics",
  "user"
]);
const taskConcurrencies = new Set<TaskConcurrency>([
  "allow",
  "replace",
  "coalesce",
  "serial"
]);
const defaultTerminalRetentionMs = 60_000;
const defaultMaxTerminalEntries = 128;
const skippedExecution = Symbol("skipped-execution");

export class TaskRegistry {
  private readonly scopes = new Map<string, ScopeRecord>();
  private readonly records = new Map<string, TaskRecord>();
  private readonly activeBySlot = new Map<string, Set<TaskRecord>>();
  private readonly slotContracts = new Map<string, SlotContract>();
  private readonly exclusiveBySlot = new Map<string, TaskRecord>();
  private readonly unsettledBySlot = new Map<string, Set<TaskRecord>>();
  private readonly serialGroups = new Map<string, SerialGroup>();
  private readonly listeners = new Set<TaskRegistryListener>();
  private readonly publicationQueue: TaskPublication[] = [];
  private readonly now: () => number;
  private readonly schedule: (callback: () => void, delayMs: number) => unknown;
  private readonly cancelSchedule: (handle: unknown) => void;
  private readonly terminalRetentionMs: number;
  private readonly maxTerminalEntries: number;
  private readonly onListenerError: (error: unknown, taskId: string) => void;
  private nextScopeGeneration = 0;
  private nextTaskSequence = 0;
  private nextTaskGeneration = 0;
  private registryRevision = 0;
  private terminalCleanupTimer: unknown | null = null;
  private terminalPruneInProgress = false;
  private terminalPruneRequested = false;
  private publishing = false;
  private disposed = false;

  constructor(options: TaskRegistryOptions = {}) {
    this.now = options.now ?? Date.now;
    this.schedule = options.schedule
      ?? ((callback, delayMs) => globalThis.setTimeout(callback, delayMs));
    this.cancelSchedule = options.cancelSchedule
      ?? ((handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>));
    this.terminalRetentionMs = normalizeNonNegativeInteger(
      options.terminalRetentionMs,
      defaultTerminalRetentionMs);
    this.maxTerminalEntries = normalizeNonNegativeInteger(
      options.maxTerminalEntries,
      defaultMaxTerminalEntries);
    this.onListenerError = options.onListenerError
      ?? ((error, taskId) => console.error(`Task '${taskId}' listener failed.`, error));
  }

  get taskCount(): number {
    this.pruneTerminalRecords();
    return this.records.size;
  }

  get activeTaskCount(): number {
    let count = 0;
    for (const record of this.records.values()) {
      if (!record.invalidated && !isTerminalTaskStatus(record.snapshot.status)) {
        count += 1;
      }
    }
    return count;
  }

  get collectionRevision(): number {
    return this.registryRevision;
  }

  openScope(scopeKey: string): TaskScope {
    this.throwIfDisposed();
    const key = normalizeIdentifier(scopeKey, "scope key");
    const previous = this.scopes.get(key);
    const record: ScopeRecord = {
      key,
      generation: ++this.nextScopeGeneration,
      open: true
    };

    // Publish the successor before cancellation callbacks can re-enter this key.
    this.scopes.set(key, record);
    if (previous?.open) {
      this.closeScopeRecord(previous, new Error(`Task scope '${key}' was reopened.`));
    }
    if (this.scopes.get(key) !== record) {
      this.closeScopeRecord(
        record,
        new Error(`Task scope '${key}' was superseded while opening.`));
    }

    const registry = this;
    const scope: TaskScope = {
      key,
      generation: record.generation,
      get closed() {
        return !record.open || registry.scopes.get(key) !== record;
      },
      run: <T>(descriptor: ScopedTaskDescriptor<T>, executor: TaskExecutor<T>) =>
        this.runInScope(record, descriptor, executor),
      close: (reason?: unknown) => this.closeScopeRecord(
        record,
        reason ?? new Error(`Task scope '${key}' was closed.`))
    };
    return Object.freeze(scope);
  }

  subscribe(listener: TaskRegistryListener): () => void {
    this.throwIfDisposed();
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  listSnapshots(): readonly TaskSnapshot[] {
    this.pruneTerminalRecords();
    return Object.freeze(
      [...this.records.values()]
        .sort((left, right) => left.sequence - right.sequence)
        .map((record) => record.snapshot));
  }

  cancel(taskId: string, reason?: unknown): boolean {
    this.throwIfDisposed();
    const id = normalizeIdentifier(taskId, "task id");
    const record = this.records.get(id);
    if (!record || isTerminalTaskStatus(record.snapshot.status)) {
      return false;
    }
    this.cancelRecord(
      record,
      "cancelled",
      reason ?? new Error(`Task '${record.descriptor.key}' was cancelled.`));
    return true;
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    this.clearTerminalCleanupTimer();

    for (const scope of [...this.scopes.values()]) {
      this.closeScopeRecord(scope, new Error("TaskRegistry disposed."));
    }
    for (const record of [...this.records.values()]) {
      if (!isTerminalTaskStatus(record.snapshot.status)) {
        this.cancelRecord(record, "cancelled", new Error("TaskRegistry disposed."));
      }
    }

    this.scopes.clear();
    this.activeBySlot.clear();
    this.slotContracts.clear();
    this.exclusiveBySlot.clear();
    this.unsettledBySlot.clear();
    this.serialGroups.clear();
    this.records.clear();
    this.listeners.clear();
    this.publicationQueue.length = 0;
  }

  private runInScope<T>(
    scope: ScopeRecord,
    descriptor: ScopedTaskDescriptor<T>,
    executor: TaskExecutor<T>
  ): TaskRun<T> {
    this.throwIfDisposed();
    if (!scope.open || this.scopes.get(scope.key) !== scope) {
      throw new Error(`Task scope '${scope.key}' is closed.`);
    }
    if (typeof executor !== "function") {
      throw new TypeError("A task executor is required.");
    }

    const normalized = normalizeDescriptor(scope.key, descriptor);
    if (normalized.descriptor.durability === "external-durable") {
      throw new Error(
        "External durable tasks are projections and cannot run a frontend executor.");
    }

    const slotKey = createSlotKey(scope.key, normalized.descriptor.key);
    const signature = createDescriptorSignature(normalized.descriptor);
    const existingContract = this.slotContracts.get(slotKey);
    if (existingContract
      && (existingContract.token !== normalized.keyToken
        || existingContract.signature !== signature)) {
      throw new Error(
        `Task slot '${scope.key}/${normalized.descriptor.key}' has a conflicting descriptor.`);
    }
    if (!existingContract) {
      this.slotContracts.set(slotKey, {
        token: normalized.keyToken,
        signature
      });
    }

    const previousActive = this.getActiveSlotRecords(slotKey);
    if (normalized.descriptor.concurrency === "coalesce" && previousActive.length > 0) {
      const authoritative = this.exclusiveBySlot.get(slotKey);
      const owner = authoritative && !isTerminalTaskStatus(authoritative.snapshot.status)
        ? authoritative
        : previousActive[0];
      return owner.handle as TaskRun<T>;
    }

    const record = this.createTaskRecord(
      scope,
      normalized.descriptor,
      slotKey,
      executor as TaskExecutor<unknown>);
    let serialGroup: SerialGroup | null = null;
    if (requiresExclusiveAuthority(record.descriptor.concurrency)) {
      this.exclusiveBySlot.set(slotKey, record);
    }
    if (record.descriptor.concurrency === "serial") {
      serialGroup = this.enqueueSerial(record);
    }

    if (record.descriptor.concurrency === "replace") {
      for (const current of previousActive) {
        this.cancelRecord(
          current,
          "superseded",
          new Error(`Task '${current.descriptor.key}' was replaced.`));
      }
    }

    if (record.snapshot.status === "queued"
      && requiresExclusiveAuthority(record.descriptor.concurrency)
      && this.exclusiveBySlot.get(slotKey) !== record) {
      this.cancelRecord(
        record,
        "superseded",
        new Error(`Task '${record.descriptor.key}' lost slot authority before start.`));
    }

    if (record.snapshot.status === "queued") {
      this.notifyUpsert(record);
      if (record.snapshot.status === "queued") {
        if (serialGroup) {
          this.scheduleSerialDrain(serialGroup);
        } else {
          this.startRecord(record);
        }
      }
    }
    this.retireSlotIfIdle(record.slotKey);
    return record.handle as TaskRun<T>;
  }

  private createTaskRecord(
    scope: ScopeRecord,
    descriptor: TaskDescriptor,
    slotKey: string,
    executor: TaskExecutor<unknown>
  ): TaskRecord {
    const sequence = ++this.nextTaskSequence;
    const generation = ++this.nextTaskGeneration;
    const id = `frontend-task-${sequence}`;
    const createdAtMs = this.readNow();
    let resolveCompletion!: (outcome: TaskOutcome<unknown>) => void;
    const completion = new Promise<TaskOutcome<unknown>>((resolve) => {
      resolveCompletion = resolve;
    });
    const serialGroupKey = descriptor.concurrency === "serial"
      ? createSerialGroupKey(descriptor, slotKey)
      : null;
    const record: TaskRecord = {
      id,
      sequence,
      generation,
      scope,
      descriptor,
      slotKey,
      serialGroupKey,
      controller: new AbortController(),
      executor,
      listeners: new Set<TaskListener>(),
      completion,
      resolveCompletion,
      handle: null as unknown as TaskRun<unknown>,
      snapshot: Object.freeze({
        id,
        descriptor,
        generation,
        scopeGeneration: scope.generation,
        status: "queued",
        revision: 1,
        progress: null,
        error: null,
        reason: null,
        createdAt: formatTimestamp(createdAtMs),
        startedAt: null,
        completedAt: null
      }),
      invalidated: false,
      executionStarted: false,
      executionSettled: false,
      terminalAtMs: null
    };
    record.handle = this.createRunHandle(record);
    this.records.set(id, record);
    let active = this.activeBySlot.get(slotKey);
    if (!active) {
      active = new Set<TaskRecord>();
      this.activeBySlot.set(slotKey, active);
    }
    active.add(record);
    return record;
  }

  private createRunHandle(record: TaskRecord): TaskRun<unknown> {
    const handle: TaskRun<unknown> = {
      id: record.id,
      descriptor: record.descriptor,
      get snapshot() {
        return record.snapshot;
      },
      completion: record.completion,
      subscribe: (listener) => {
        record.listeners.add(listener);
        return () => record.listeners.delete(listener);
      },
      cancel: (reason?: unknown) => this.cancelRecord(
        record,
        "cancelled",
        reason ?? new Error(`Task '${record.descriptor.key}' was cancelled.`))
    };
    return Object.freeze(handle);
  }

  private startRecord(record: TaskRecord): void {
    if (record.snapshot.status !== "queued") {
      return;
    }
    if (!this.isScopeCurrent(record)) {
      this.cancelRecord(
        record,
        "cancelled",
        new Error(`Task scope '${record.scope.key}' is no longer current.`));
      return;
    }
    if (requiresExclusiveAuthority(record.descriptor.concurrency)
      && this.exclusiveBySlot.get(record.slotKey) !== record) {
      this.cancelRecord(
        record,
        "superseded",
        new Error(`Task '${record.descriptor.key}' lost slot authority.`));
      return;
    }

    record.executionStarted = true;
    this.addUnsettledRecord(record);
    this.publish(record, {
      status: "running",
      startedAt: formatTimestamp(this.readNow())
    });
    const context = this.createExecutionContext(record);

    let execution: Promise<unknown | typeof skippedExecution>;
    try {
      execution = this.canCommit(record)
        ? Promise.resolve(record.executor(context))
        : Promise.resolve(skippedExecution);
    } catch (error: unknown) {
      execution = Promise.reject(error);
    }

    void execution
      .then((value) => {
        if (value === skippedExecution || isTerminalTaskStatus(record.snapshot.status)) {
          return;
        }
        if (!this.canCommit(record)) {
          this.cancelRecord(
            record,
            "superseded",
            new Error(`Task '${record.descriptor.key}' lost commit authority.`));
          return;
        }
        this.completeRecord(record, { status: "succeeded", value });
      })
      .catch((error: unknown) => {
        if (isTerminalTaskStatus(record.snapshot.status)) {
          return;
        }
        this.completeRecord(record, { status: "failed", error });
      })
      .finally(() => this.handleExecutionSettled(record));
  }

  private createExecutionContext(record: TaskRecord): TaskExecutionContext {
    return Object.freeze({
      signal: record.controller.signal,
      isCurrent: () => this.canCommit(record),
      commit: (effect: () => void) => {
        if (!this.canCommit(record)) {
          return false;
        }
        effect();
        return true;
      },
      reportProgress: (progress: TaskProgress) => {
        if (!this.canCommit(record)) {
          return false;
        }
        this.publish(record, { progress: normalizeProgress(progress) });
        return true;
      }
    });
  }

  private enqueueSerial(record: TaskRecord): SerialGroup {
    const key = record.serialGroupKey!;
    let group = this.serialGroups.get(key);
    if (!group) {
      group = {
        key,
        queue: [],
        running: null,
        drainScheduled: false
      };
      this.serialGroups.set(key, group);
    }
    group.queue.push(record);
    return group;
  }

  private scheduleSerialDrain(group: SerialGroup): void {
    if (this.disposed
      || group.drainScheduled
      || this.serialGroups.get(group.key) !== group) {
      return;
    }
    group.drainScheduled = true;
    queueMicrotask(() => {
      group.drainScheduled = false;
      this.drainSerialGroup(group);
    });
  }

  private drainSerialGroup(group: SerialGroup): void {
    if (this.disposed
      || group.running
      || this.serialGroups.get(group.key) !== group) {
      return;
    }
    while (group.queue.length > 0) {
      const record = group.queue.shift()!;
      if (record.snapshot.status !== "queued") {
        continue;
      }
      group.running = record;
      this.startRecord(record);
      return;
    }
    this.deleteEmptySerialGroup(group);
  }

  private unlinkQueuedSerialRecord(record: TaskRecord): void {
    if (!record.serialGroupKey) {
      return;
    }
    const group = this.serialGroups.get(record.serialGroupKey);
    if (!group) {
      return;
    }
    if (group.running === record) {
      group.running = null;
    } else {
      const index = group.queue.indexOf(record);
      if (index >= 0) {
        group.queue.splice(index, 1);
      }
    }
    if (group.running || group.queue.length > 0) {
      this.scheduleSerialDrain(group);
    } else {
      this.deleteEmptySerialGroup(group);
    }
  }

  private deleteEmptySerialGroup(group: SerialGroup): void {
    if (!group.running
      && group.queue.length === 0
      && this.serialGroups.get(group.key) === group) {
      this.serialGroups.delete(group.key);
    }
  }

  private handleExecutionSettled(record: TaskRecord): void {
    if (record.executionSettled) {
      return;
    }
    record.executionSettled = true;
    this.removeUnsettledRecord(record);
    if (record.serialGroupKey) {
      const group = this.serialGroups.get(record.serialGroupKey);
      if (group?.running === record) {
        group.running = null;
        if (group.queue.length > 0) {
          this.scheduleSerialDrain(group);
        } else {
          this.deleteEmptySerialGroup(group);
        }
      }
    }
    this.retireSlotIfIdle(record.slotKey);
  }

  private cancelRecord(
    record: TaskRecord,
    status: "cancelled" | "superseded",
    reason: unknown
  ): void {
    if (isTerminalTaskStatus(record.snapshot.status) || record.invalidated) {
      return;
    }
    record.invalidated = true;
    this.removeActiveRecord(record);
    if (!record.executionStarted) {
      record.executionSettled = true;
      this.unlinkQueuedSerialRecord(record);
    }
    if (!record.controller.signal.aborted) {
      record.controller.abort(reason);
    }
    this.completeRecord(record, { status, reason });
  }

  private completeRecord(record: TaskRecord, outcome: TaskOutcome<unknown>): void {
    if (isTerminalTaskStatus(record.snapshot.status)) {
      return;
    }
    const terminalAt = this.readNow();
    record.terminalAtMs = terminalAt;
    this.publish(record, {
      status: outcome.status,
      error: outcome.status === "failed" ? outcome.error : null,
      reason: outcome.status === "cancelled" || outcome.status === "superseded"
        ? outcome.reason
        : null,
      completedAt: formatTimestamp(terminalAt)
    });
    this.removeActiveRecord(record);
    if (this.exclusiveBySlot.get(record.slotKey) === record) {
      this.exclusiveBySlot.delete(record.slotKey);
    }
    record.resolveCompletion(Object.freeze(outcome));
    this.retireSlotIfIdle(record.slotKey);
    this.pruneTerminalRecords();
  }

  private closeScopeRecord(scope: ScopeRecord, reason: unknown): void {
    if (!scope.open) {
      return;
    }
    scope.open = false;
    if (this.scopes.get(scope.key) === scope) {
      this.scopes.delete(scope.key);
    }
    for (const record of [...this.records.values()]) {
      if (record.scope === scope && !isTerminalTaskStatus(record.snapshot.status)) {
        this.cancelRecord(record, "cancelled", reason);
      }
    }
  }

  private publish(
    record: TaskRecord,
    update: Partial<Pick<
      TaskSnapshot,
      "status" | "progress" | "error" | "reason" | "startedAt" | "completedAt"
    >>
  ): void {
    record.snapshot = Object.freeze({
      ...record.snapshot,
      ...update,
      revision: record.snapshot.revision + 1
    });
    this.notifyUpsert(record);
  }

  private notifyUpsert(record: TaskRecord): void {
    const change: TaskRegistryChange = Object.freeze({
      kind: "upsert",
      revision: ++this.registryRevision,
      snapshot: record.snapshot
    });
    this.enqueuePublication({
      kind: "upsert",
      record,
      snapshot: record.snapshot,
      change
    });
  }

  private notifyRemoval(taskId: string): void {
    const change: TaskRegistryChange = Object.freeze({
      kind: "remove",
      revision: ++this.registryRevision,
      taskId
    });
    this.enqueuePublication({ kind: "remove", taskId, change });
  }

  private enqueuePublication(publication: TaskPublication): void {
    this.publicationQueue.push(publication);
    if (this.publishing) {
      return;
    }

    this.publishing = true;
    try {
      while (this.publicationQueue.length > 0) {
        const current = this.publicationQueue.shift()!;
        if (current.kind === "upsert") {
          for (const listener of [...current.record.listeners]) {
            this.invokeListener(
              () => listener(current.snapshot),
              current.record.id);
          }
        }
        for (const listener of [...this.listeners]) {
          this.invokeListener(
            () => listener(current.change),
            current.kind === "upsert" ? current.record.id : current.taskId);
        }
      }
    } finally {
      this.publishing = false;
    }
  }

  private invokeListener(callback: () => void, taskId: string): void {
    try {
      callback();
    } catch (error: unknown) {
      try {
        this.onListenerError(error, taskId);
      } catch {
        // Diagnostics cannot take ownership of task publication.
      }
    }
  }

  private canCommit(record: TaskRecord): boolean {
    return !this.disposed
      && !record.invalidated
      && record.snapshot.status === "running"
      && this.isScopeCurrent(record)
      && (!requiresExclusiveAuthority(record.descriptor.concurrency)
        || this.exclusiveBySlot.get(record.slotKey) === record);
  }

  private isScopeCurrent(record: TaskRecord): boolean {
    return record.scope.open
      && this.scopes.get(record.scope.key) === record.scope
      && record.scope.generation === record.snapshot.scopeGeneration;
  }

  private getActiveSlotRecords(slotKey: string): TaskRecord[] {
    const active = this.activeBySlot.get(slotKey);
    if (!active) {
      return [];
    }
    const result: TaskRecord[] = [];
    for (const record of active) {
      if (isTerminalTaskStatus(record.snapshot.status)) {
        active.delete(record);
      } else {
        result.push(record);
      }
    }
    if (active.size === 0) {
      this.activeBySlot.delete(slotKey);
    }
    return result;
  }

  private removeActiveRecord(record: TaskRecord): void {
    const active = this.activeBySlot.get(record.slotKey);
    active?.delete(record);
    if (active?.size === 0) {
      this.activeBySlot.delete(record.slotKey);
    }
  }

  private addUnsettledRecord(record: TaskRecord): void {
    let unsettled = this.unsettledBySlot.get(record.slotKey);
    if (!unsettled) {
      unsettled = new Set<TaskRecord>();
      this.unsettledBySlot.set(record.slotKey, unsettled);
    }
    unsettled.add(record);
  }

  private removeUnsettledRecord(record: TaskRecord): void {
    const unsettled = this.unsettledBySlot.get(record.slotKey);
    unsettled?.delete(record);
    if (unsettled?.size === 0) {
      this.unsettledBySlot.delete(record.slotKey);
    }
  }

  private retireSlotIfIdle(slotKey: string): void {
    if ((this.activeBySlot.get(slotKey)?.size ?? 0) > 0
      || (this.unsettledBySlot.get(slotKey)?.size ?? 0) > 0) {
      return;
    }
    this.activeBySlot.delete(slotKey);
    this.unsettledBySlot.delete(slotKey);
    this.slotContracts.delete(slotKey);
    const authority = this.exclusiveBySlot.get(slotKey);
    if (!authority || isTerminalTaskStatus(authority.snapshot.status)) {
      this.exclusiveBySlot.delete(slotKey);
    }
  }

  private pruneTerminalRecords(): void {
    if (this.disposed) {
      return;
    }
    if (this.terminalPruneInProgress) {
      this.terminalPruneRequested = true;
      return;
    }

    this.terminalPruneInProgress = true;
    try {
      this.clearTerminalCleanupTimer();
      do {
        this.terminalPruneRequested = false;
        this.pruneTerminalRecordsPass();
      } while (this.terminalPruneRequested && !this.disposed);

      if (!this.disposed) {
        this.scheduleTerminalCleanup();
      }
    } finally {
      this.terminalPruneInProgress = false;
    }
  }

  private pruneTerminalRecordsPass(): void {
    const now = this.readNow();
    const terminals = [...this.records.values()]
      .filter((record) => record.terminalAtMs !== null)
      .sort((left, right) =>
        left.terminalAtMs! - right.terminalAtMs!
        || left.sequence - right.sequence);

    for (const record of terminals) {
      if (this.terminalRetentionMs === 0
        || now - record.terminalAtMs! >= this.terminalRetentionMs) {
        this.removeRecord(record);
      }
    }

    const retained = terminals.filter((record) => this.records.has(record.id));
    while (retained.length > this.maxTerminalEntries) {
      this.removeRecord(retained.shift()!);
    }
  }

  private scheduleTerminalCleanup(): void {
    if (this.terminalRetentionMs === 0) {
      return;
    }
    let oldestTerminalAt: number | null = null;
    for (const record of this.records.values()) {
      if (record.terminalAtMs !== null
        && (oldestTerminalAt === null || record.terminalAtMs < oldestTerminalAt)) {
        oldestTerminalAt = record.terminalAtMs;
      }
    }
    if (oldestTerminalAt === null) {
      return;
    }
    const now = this.readNow();
    const delay = Math.max(
      1,
      oldestTerminalAt + this.terminalRetentionMs - now);
    this.terminalCleanupTimer = this.schedule(() => {
      this.terminalCleanupTimer = null;
      this.pruneTerminalRecords();
    }, delay);
  }

  private removeRecord(record: TaskRecord): void {
    if (this.records.delete(record.id)) {
      this.notifyRemoval(record.id);
    }
  }

  private clearTerminalCleanupTimer(): void {
    if (this.terminalCleanupTimer === null) {
      return;
    }
    this.cancelSchedule(this.terminalCleanupTimer);
    this.terminalCleanupTimer = null;
  }

  private readNow(): number {
    const value = this.now();
    return Number.isFinite(value) ? value : Date.now();
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error("TaskRegistry is disposed.");
    }
  }
}

function normalizeDescriptor<T>(
  scopeKey: string,
  descriptor: ScopedTaskDescriptor<T>
): NormalizedTaskDescriptor<T> {
  const keyToken = descriptor?.key;
  const key = normalizeIdentifier(keyToken?.value, "task key");
  const groupKey = descriptor.groupKey === undefined
    ? undefined
    : normalizeIdentifier(descriptor.groupKey, "task group key");
  if (!taskDurabilities.has(descriptor.durability)) {
    throw new Error(`Unsupported task durability '${String(descriptor.durability)}'.`);
  }
  if (!taskVisibilities.has(descriptor.visibility)) {
    throw new Error(`Unsupported task visibility '${String(descriptor.visibility)}'.`);
  }
  if (!taskConcurrencies.has(descriptor.concurrency)) {
    throw new Error(`Unsupported task concurrency '${String(descriptor.concurrency)}'.`);
  }
  return Object.freeze({
    keyToken,
    descriptor: Object.freeze({
      key,
      title: normalizeOptionalTaskTitle(descriptor.title),
      scopeKey,
      groupKey,
      durability: descriptor.durability,
      visibility: descriptor.visibility,
      concurrency: descriptor.concurrency
    })
  });
}

function normalizeOptionalTaskTitle(value: string | undefined): string | undefined {
  if (value === undefined) {
    return undefined;
  }
  const normalized = value.trim();
  if (!normalized || normalized.includes("\0") || normalized.length > 160) {
    throw new Error("A valid task title is required when title is provided.");
  }
  return normalized;
}

function normalizeIdentifier(value: string, name: string): string {
  const normalized = typeof value === "string" ? value.trim() : "";
  if (!normalized || normalized.includes("\0")) {
    throw new Error(`A valid ${name} is required.`);
  }
  return normalized;
}

function createSlotKey(scopeKey: string, taskKey: string): string {
  return JSON.stringify([scopeKey, taskKey]);
}

function createSerialGroupKey(descriptor: TaskDescriptor, slotKey: string): string {
  return descriptor.groupKey
    ? JSON.stringify(["group", descriptor.groupKey])
    : JSON.stringify(["slot", slotKey]);
}

function createDescriptorSignature(descriptor: TaskDescriptor): string {
  return JSON.stringify([
    descriptor.durability,
    descriptor.visibility,
    descriptor.concurrency,
    descriptor.groupKey ?? null
  ]);
}

function requiresExclusiveAuthority(concurrency: TaskConcurrency): boolean {
  return concurrency === "replace" || concurrency === "coalesce";
}

function normalizeProgress(progress: TaskProgress): TaskProgress {
  if (!progress || typeof progress !== "object") {
    throw new TypeError("Task progress must be an object.");
  }
  const completed = normalizeProgressNumber(progress.completed, "completed");
  const total = normalizeProgressNumber(progress.total, "total");
  if (completed !== null && total !== null && completed > total) {
    throw new RangeError("Task progress completed cannot exceed total.");
  }
  return Object.freeze({
    phase: normalizeOptionalText(progress.phase),
    message: normalizeOptionalText(progress.message),
    completed,
    total
  });
}

function normalizeProgressNumber(
  value: number | null | undefined,
  name: string
): number | null {
  if (value === undefined || value === null) {
    return null;
  }
  if (!Number.isFinite(value) || value < 0) {
    throw new RangeError(`Task progress ${name} must be a non-negative number.`);
  }
  return value;
}

function normalizeOptionalText(value: string | null | undefined): string | null {
  if (value === undefined || value === null) {
    return null;
  }
  if (typeof value !== "string") {
    throw new TypeError("Task progress text must be a string.");
  }
  const normalized = value.trim();
  return normalized || null;
}

function normalizeNonNegativeInteger(
  value: number | undefined,
  fallback: number
): number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0
    ? Math.floor(value)
    : fallback;
}

function formatTimestamp(milliseconds: number): string {
  return new Date(milliseconds).toISOString();
}
