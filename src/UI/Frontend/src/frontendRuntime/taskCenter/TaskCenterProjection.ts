import type { OperationSnapshot } from "../../types.ts";
import type {
  OperationRegistrySnapshot
} from "../operations/OperationRegistry.ts";
import { isTerminalOperation } from "../operations/operationMerge.ts";
import type { TaskRegistryChange, TaskSnapshot } from "../task/TaskSnapshot.ts";
import { isTerminalTaskStatus } from "../task/TaskSnapshot.ts";
import { uiText } from "../../text.ts";

export type TaskCenterItemSource = "frontend" | "operation";
export type TaskCenterItemVisibility = "hidden" | "diagnostics" | "user";
export type TaskCenterOperationState =
  | "loading"
  | "available"
  | "disconnected"
  | "error";
export type TaskCenterItemDurability =
  | "transient"
  | "frontend-session"
  | "external-durable";

export interface TaskCenterProgress {
  readonly percent: number | null;
  readonly completed: number | string | null;
  readonly total: number | string | null;
  readonly stage: string | null;
  readonly message: string | null;
}

export interface TaskCenterItem {
  readonly id: string;
  readonly ownerId: string;
  readonly source: TaskCenterItemSource;
  readonly title: string;
  readonly kind: string;
  readonly domainKey: string | null;
  readonly status: string;
  readonly active: boolean;
  readonly visibility: TaskCenterItemVisibility;
  readonly durability: TaskCenterItemDurability;
  readonly createdAt: string;
  readonly startedAt: string | null;
  readonly updatedAt: string;
  readonly completedAt: string | null;
  readonly progress: TaskCenterProgress | null;
  readonly resultSummary: string | null;
  readonly errorSummary: string | null;
  readonly cancelable: boolean;
  readonly actionBlockedReason: string | null;
  readonly backendDisconnected: boolean;
  readonly stateUncertain: boolean;
}

export interface TaskCenterSnapshot {
  readonly items: readonly TaskCenterItem[];
  readonly activeUserCount: number;
  readonly operationState: TaskCenterOperationState;
  readonly revision: number;
}

interface FrontendTaskSource {
  listSnapshots(): readonly TaskSnapshot[];
  subscribe(listener: (change: TaskRegistryChange) => void): () => void;
  cancel(taskId: string, reason?: unknown): boolean;
}

interface OperationProjectionSource {
  readonly snapshot: OperationRegistrySnapshot;
  subscribe(listener: (snapshot: OperationRegistrySnapshot) => void): () => void;
  cancel(id: string, signal?: AbortSignal): Promise<OperationSnapshot>;
}

export interface TaskCenterProjectionOptions {
  readonly onListenerError?: (error: unknown) => void;
}

type TaskCenterListener = (snapshot: TaskCenterSnapshot) => void;

export class TaskCenterProjection {
  private readonly taskSource: FrontendTaskSource;
  private readonly operationSource: OperationProjectionSource;
  private readonly frontendTasks = new Map<string, TaskSnapshot>();
  private readonly listeners = new Set<TaskCenterListener>();
  private readonly publicationQueue: TaskCenterSnapshot[] = [];
  private readonly unsubscribeTasks: () => void;
  private readonly unsubscribeOperations: () => void;
  private readonly onListenerError: (error: unknown) => void;
  private operationSnapshot: OperationRegistrySnapshot;
  private snapshotValue: TaskCenterSnapshot;
  private publishing = false;
  private disposed = false;

  constructor(
    taskSource: FrontendTaskSource,
    operationSource: OperationProjectionSource,
    options: TaskCenterProjectionOptions = {}
  ) {
    this.taskSource = taskSource;
    this.operationSource = operationSource;
    this.onListenerError = options.onListenerError
      ?? ((error) => console.error("TaskCenterProjection listener failed.", error));
    for (const snapshot of taskSource.listSnapshots()) {
      this.frontendTasks.set(snapshot.id, snapshot);
    }
    this.operationSnapshot = operationSource.snapshot;
    this.snapshotValue = this.buildSnapshot(0);
    this.unsubscribeTasks = taskSource.subscribe(
      (change) => this.handleTaskChange(change));
    this.unsubscribeOperations = operationSource.subscribe(
      (snapshot) => this.handleOperationSnapshot(snapshot));
  }

  get snapshot(): TaskCenterSnapshot {
    return this.snapshotValue;
  }

  subscribe(listener: TaskCenterListener): () => void {
    this.throwIfDisposed();
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async cancel(itemId: string, signal?: AbortSignal): Promise<boolean> {
    this.throwIfDisposed();
    const item = this.snapshotValue.items.find((candidate) => candidate.id === itemId);
    if (!item) {
      return false;
    }
    if (!item.cancelable) {
      throw new Error(item.actionBlockedReason ?? uiText.taskCenter.blocked.cannotCancel);
    }
    if (item.source === "frontend") {
      return this.taskSource.cancel(
        item.ownerId,
        new Error("Task cancelled from Task Center."));
    }
    await this.operationSource.cancel(item.ownerId, signal);
    return true;
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    this.unsubscribeTasks();
    this.unsubscribeOperations();
    this.frontendTasks.clear();
    this.listeners.clear();
    this.publicationQueue.length = 0;
  }

  private handleTaskChange(change: TaskRegistryChange): void {
    if (this.disposed) {
      return;
    }
    if (change.kind === "upsert") {
      const current = this.frontendTasks.get(change.snapshot.id);
      if (!current || change.snapshot.revision > current.revision) {
        this.frontendTasks.set(change.snapshot.id, change.snapshot);
      } else if (change.snapshot.revision === current.revision
        && change.snapshot !== current) {
        throw new Error(
          `Frontend task '${change.snapshot.id}' conflicted at revision ${current.revision}.`);
      } else {
        return;
      }
    } else {
      this.frontendTasks.delete(change.taskId);
    }
    this.publish();
  }

  private handleOperationSnapshot(snapshot: OperationRegistrySnapshot): void {
    if (this.disposed || snapshot.revision <= this.operationSnapshot.revision) {
      return;
    }
    this.operationSnapshot = snapshot;
    this.publish();
  }

  private publish(): void {
    this.snapshotValue = this.buildSnapshot(this.snapshotValue.revision + 1);
    this.publicationQueue.push(this.snapshotValue);
    if (this.publishing) {
      return;
    }
    this.publishing = true;
    try {
      while (this.publicationQueue.length > 0) {
        const snapshot = this.publicationQueue.shift()!;
        for (const listener of [...this.listeners]) {
          try {
            listener(snapshot);
          } catch (error) {
            try {
              this.onListenerError(error);
            } catch {
              // Diagnostics cannot own task-center publication.
            }
          }
        }
      }
    } finally {
      this.publishing = false;
    }
  }

  private buildSnapshot(revision: number): TaskCenterSnapshot {
    const frontend = [...this.frontendTasks.values()].map(projectFrontendTask);
    const operations = this.operationSnapshot.operations.map((operation) =>
      projectOperation(operation, this.operationSnapshot));
    const items = Object.freeze([...frontend, ...operations].sort(compareItems));
    return Object.freeze({
      items,
      activeUserCount: items.filter((item) =>
        item.visibility === "user" && item.active).length,
      operationState: projectOperationState(this.operationSnapshot),
      revision
    });
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error("TaskCenterProjection disposed.");
    }
  }
}

function projectFrontendTask(snapshot: TaskSnapshot): TaskCenterItem {
  const active = !isTerminalTaskStatus(snapshot.status);
  const progress = snapshot.progress
    ? Object.freeze({
      percent: progressPercent(snapshot.progress.completed, snapshot.progress.total),
      completed: snapshot.progress.completed ?? null,
      total: snapshot.progress.total ?? null,
      stage: snapshot.progress.phase ?? null,
      message: snapshot.progress.message ?? null
    })
    : null;
  return Object.freeze({
    id: `frontend:${snapshot.id}`,
    ownerId: snapshot.id,
    source: "frontend",
    title: taskTitle(snapshot),
    kind: snapshot.descriptor.key,
    domainKey: snapshot.descriptor.groupKey ?? snapshot.descriptor.scopeKey,
    status: snapshot.status,
    active,
    visibility: snapshot.descriptor.visibility,
    durability: snapshot.descriptor.durability,
    createdAt: snapshot.createdAt,
    startedAt: snapshot.startedAt,
    updatedAt: snapshot.completedAt ?? snapshot.startedAt ?? snapshot.createdAt,
    completedAt: snapshot.completedAt,
    progress,
    resultSummary: null,
    errorSummary: unknownSummary(snapshot.error ?? snapshot.reason),
    cancelable: active,
    actionBlockedReason: active ? null : uiText.taskCenter.blocked.taskFinished,
    backendDisconnected: false,
    stateUncertain: false
  });
}

function projectOperation(
  operation: OperationSnapshot,
  registry: OperationRegistrySnapshot
): TaskCenterItem {
  const active = !isTerminalOperation(operation);
  const disconnected = operationBackendDisconnected(registry);
  const cancelable = active
    && registry.actionsEnabled
    && !operation.cancelRequested
    && operation.state !== "cancelPending";
  return Object.freeze({
    id: `operation:${operation.id}`,
    ownerId: operation.id,
    source: "operation",
    title: operation.title?.trim() || operation.kind,
    kind: operation.kind,
    domainKey: operation.domainKey,
    status: operation.state,
    active,
    visibility: "user",
    durability: "external-durable",
    createdAt: operation.createdAt,
    startedAt: null,
    updatedAt: operation.updatedAt,
    completedAt: operation.completedAt,
    progress: operation.progress
      ? Object.freeze({
        percent: operation.progress.percent,
        completed: operation.progress.bytesDone,
        total: operation.progress.bytesTotal,
        stage: operation.progress.stage,
        message: operation.progress.message
      })
      : null,
    resultSummary: operation.result,
    errorSummary: operation.error,
    cancelable,
    actionBlockedReason: cancelable
      ? null
      : disconnected
        ? uiText.taskCenter.blocked.sessionNotSynced
        : operation.cancelRequested || operation.state === "cancelPending"
          ? uiText.taskCenter.blocked.cancelRequested
          : active
            ? uiText.taskCenter.blocked.operationCannotCancel
            : uiText.taskCenter.blocked.operationFinished,
    backendDisconnected: disconnected,
    stateUncertain: operation.state === "stateUncertain"
  });
}

function operationBackendDisconnected(snapshot: OperationRegistrySnapshot): boolean {
  const state = projectOperationState(snapshot);
  return state === "disconnected" || state === "error";
}

function projectOperationState(
  snapshot: OperationRegistrySnapshot
): TaskCenterOperationState {
  if (snapshot.status === "disposed") {
    return "disconnected";
  }
  if (snapshot.status === "ready") {
    return "available";
  }
  return "loading";
}

function taskTitle(snapshot: TaskSnapshot): string {
  return snapshot.descriptor.title ?? snapshot.descriptor.key
    .split(/[._:-]+/u)
    .filter(Boolean)
    .join(" ");
}

function progressPercent(
  completed: number | null | undefined,
  total: number | null | undefined
): number | null {
  if (completed === undefined || completed === null
    || total === undefined || total === null || total <= 0) {
    return null;
  }
  return Math.max(0, Math.min(100, completed / total * 100));
}

function unknownSummary(value: unknown): string | null {
  if (value === null || value === undefined) {
    return null;
  }
  if (value instanceof Error) {
    return value.message || value.name;
  }
  return typeof value === "string" ? value : String(value);
}

function compareItems(left: TaskCenterItem, right: TaskCenterItem): number {
  const created = Date.parse(right.createdAt) - Date.parse(left.createdAt);
  return created || left.id.localeCompare(right.id);
}
