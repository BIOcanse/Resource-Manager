import type { OperationSnapshot } from "../../types.ts";
import {
  operationSnapshotDecoder,
  type HostManagerOperationsState
} from "../../data/operations/operationsStateDecoder.ts";
import type { RequestClient } from "../request/RequestClient.ts";
import type { CurrentValueSource } from "../push/PushValueSourceFamily.ts";
import { isTerminalOperation } from "./operationMerge.ts";

export type OperationRegistryStatus =
  | "loading"
  | "ready"
  | "disposed";

export interface OperationRegistrySnapshot {
  readonly status: OperationRegistryStatus;
  readonly sourcePublicationRevision: string | null;
  readonly capturedAt: string | null;
  readonly operations: readonly OperationSnapshot[];
  readonly actionsEnabled: boolean;
  readonly error: unknown | null;
  readonly revision: number;
}

export type OperationInvalidationTarget =
  | "components"
  | "dependencies"
  | "software"
  | "migration-records"
  | "migration-sessions";

export interface OperationInvalidation {
  readonly operationId: string;
  readonly kind: string;
  readonly domainKey: string | null;
  readonly targets: readonly OperationInvalidationTarget[];
}

export interface OperationCommandDescriptor {
  readonly key: string;
  readonly url: string;
  readonly body: unknown;
  readonly fallbackError: string;
  readonly signal?: AbortSignal;
}

export interface OperationRegistryOptions {
  readonly onListenerError?: (error: unknown, source: string) => void;
}

interface OperationRequestClient {
  execute: RequestClient["execute"];
}

type RegistryListener = (snapshot: OperationRegistrySnapshot) => void;
type InvalidationListener = (invalidation: OperationInvalidation) => void;
type Publication =
  | { readonly kind: "snapshot"; readonly snapshot: OperationRegistrySnapshot }
  | { readonly kind: "invalidation"; readonly invalidation: OperationInvalidation };

export class OperationRegistry {
  private readonly requestClient: OperationRequestClient;
  private readonly source: CurrentValueSource<HostManagerOperationsState>;
  private readonly listeners = new Set<RegistryListener>();
  private readonly invalidationListeners = new Set<InvalidationListener>();
  private readonly publicationQueue: Publication[] = [];
  private readonly entries = new Map<string, OperationSnapshot>();
  private readonly terminalInvalidations = new Set<string>();
  private readonly onListenerError: (error: unknown, source: string) => void;
  private readonly unsubscribeSource: () => void;
  private hasCurrentValue = false;
  private sourcePublicationRevision: string | null = null;
  private capturedAt: string | null = null;
  private publishing = false;
  private disposed = false;
  private snapshotValue: OperationRegistrySnapshot;

  constructor(
    requestClient: OperationRequestClient,
    source: CurrentValueSource<HostManagerOperationsState>,
    options: OperationRegistryOptions = {}) {
    this.requestClient = requestClient;
    this.source = source;
    this.onListenerError = options.onListenerError
      ?? ((error, sourceName) => console.error(
        `OperationRegistry ${sourceName} listener failed.`,
        error));
    this.snapshotValue = Object.freeze({
      status: "loading",
      sourcePublicationRevision: null,
      capturedAt: null,
      operations: Object.freeze([]),
      actionsEnabled: false,
      error: null,
      revision: 0
    });
    this.unsubscribeSource = this.source.subscribe(
      (value) => this.handleCurrentValue(value));
  }

  get snapshot(): OperationRegistrySnapshot {
    return this.snapshotValue;
  }

  get(id: string): OperationSnapshot | null {
    return this.entries.get(id) ?? null;
  }

  list(): readonly OperationSnapshot[] {
    return this.snapshotValue.operations;
  }

  subscribe(listener: RegistryListener): () => void {
    this.throwIfDisposed();
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  subscribeInvalidations(listener: InvalidationListener): () => void {
    this.throwIfDisposed();
    this.invalidationListeners.add(listener);
    return () => this.invalidationListeners.delete(listener);
  }

  async submit(descriptor: OperationCommandDescriptor): Promise<OperationSnapshot> {
    this.throwIfDisposed();
    if (!this.snapshotValue.actionsEnabled) {
      throw new Error("后台操作状态尚未收到当前值。");
    }
    const outcome = await this.requestClient.execute({
      key: descriptor.key,
      url: descriptor.url,
      fallbackError: descriptor.fallbackError,
      decoder: operationSnapshotDecoder,
      signal: descriptor.signal,
      request: {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(descriptor.body)
      }
    });
    return outcome.value;
  }

  cancel(id: string, signal?: AbortSignal): Promise<OperationSnapshot> {
    const operationId = normalizeOperationId(id);
    return this.submit({
      key: `host-manager.operation.cancel:${operationId}`,
      url: `/api/operations/${encodeURIComponent(operationId)}/cancel`,
      body: {},
      fallbackError: "取消操作失败",
      signal
    });
  }

  waitForTerminal(
    id: string,
    signal?: AbortSignal
  ): Promise<OperationSnapshot> {
    this.throwIfDisposed();
    const operationId = normalizeOperationId(id);
    const current = this.get(operationId);
    if (current && isTerminalOperation(current)) {
      return Promise.resolve(current);
    }
    if (signal?.aborted) {
      return Promise.reject(abortReason(signal));
    }

    return new Promise<OperationSnapshot>((resolve, reject) => {
      let settled = false;
      let seen = current !== null;
      const finish = (callback: () => void) => {
        if (settled) {
          return;
        }
        settled = true;
        unsubscribe();
        signal?.removeEventListener("abort", handleAbort);
        callback();
      };
      const handleAbort = () => finish(() => reject(abortReason(signal!)));
      const unsubscribe = this.subscribe((snapshot) => {
        const operation = snapshot.operations.find((item) => item.id === operationId);
        if (operation) {
          seen = true;
          if (isTerminalOperation(operation)) {
            finish(() => resolve(operation));
          }
          return;
        }
        if (snapshot.status === "disposed") {
          finish(() => reject(new Error("OperationRegistry disposed.")));
        } else if (seen && snapshot.status === "ready") {
          finish(() => reject(new Error(`后台操作已经从当前值中移除：${operationId}`)));
        }
      });
      signal?.addEventListener("abort", handleAbort, { once: true });
      if (signal?.aborted) {
        handleAbort();
      }
    });
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    this.unsubscribeSource();
    this.publishSnapshot("disposed");
    this.entries.clear();
    this.terminalInvalidations.clear();
    this.listeners.clear();
    this.invalidationListeners.clear();
    this.publicationQueue.length = 0;
  }

  private handleCurrentValue(value: HostManagerOperationsState): void {
    if (this.disposed) {
      return;
    }
    const previous = new Map(this.entries);
    const hadCurrentValue = this.hasCurrentValue;
    this.entries.clear();
    for (const operation of value.operations) {
      this.entries.set(operation.id, operation);
    }
    this.sourcePublicationRevision = value.publicationRevision;
    this.capturedAt = value.capturedAt;
    this.hasCurrentValue = true;
    this.publishSnapshot("ready");

    if (!hadCurrentValue) {
      return;
    }
    for (const operation of value.operations) {
      const old = previous.get(operation.id);
      if ((!old || !isTerminalOperation(old)) && isTerminalOperation(operation)) {
        this.publishTerminalInvalidation(operation);
      }
    }
  }

  private publishSnapshot(status: OperationRegistryStatus): void {
    const operations = Object.freeze([...this.entries.values()]
      .sort((left, right) =>
        Date.parse(right.updatedAt) - Date.parse(left.updatedAt)
        || left.id.localeCompare(right.id)));
    this.snapshotValue = Object.freeze({
      status,
      sourcePublicationRevision: this.sourcePublicationRevision,
      capturedAt: this.capturedAt,
      operations,
      actionsEnabled: status === "ready" && this.hasCurrentValue,
      error: null,
      revision: this.snapshotValue.revision + 1
    });
    this.enqueuePublication({ kind: "snapshot", snapshot: this.snapshotValue });
  }

  private publishTerminalInvalidation(operation: OperationSnapshot): void {
    if (this.terminalInvalidations.has(operation.id)) {
      return;
    }
    this.terminalInvalidations.add(operation.id);
    const targets = invalidationTargets(operation.kind);
    if (targets.length === 0) {
      return;
    }
    this.enqueuePublication({
      kind: "invalidation",
      invalidation: Object.freeze({
        operationId: operation.id,
        kind: operation.kind,
        domainKey: operation.domainKey,
        targets
      })
    });
  }

  private enqueuePublication(publication: Publication): void {
    this.publicationQueue.push(publication);
    if (this.publishing) {
      return;
    }
    this.publishing = true;
    try {
      while (this.publicationQueue.length > 0) {
        const current = this.publicationQueue.shift()!;
        const listeners = current.kind === "snapshot"
          ? [...this.listeners]
          : [...this.invalidationListeners];
        for (const listener of listeners) {
          try {
            if (current.kind === "snapshot") {
              (listener as RegistryListener)(current.snapshot);
            } else {
              (listener as InvalidationListener)(current.invalidation);
            }
          } catch (error) {
            this.reportListenerError(
              error,
              current.kind === "snapshot" ? "snapshot" : "invalidation");
          }
        }
      }
    } finally {
      this.publishing = false;
    }
  }

  private reportListenerError(error: unknown, source: string): void {
    try {
      this.onListenerError(error, source);
    } catch {
      // Diagnostics cannot own operation publication.
    }
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error("OperationRegistry disposed.");
    }
  }
}

function normalizeOperationId(value: string): string {
  const id = value.trim();
  if (!/^[0-9a-f]{32}$/.test(id) || /^0{32}$/.test(id)) {
    throw new Error("A valid operation id is required.");
  }
  return id;
}

function abortReason(signal: AbortSignal): Error {
  return signal.reason instanceof Error
    ? signal.reason
    : new Error("操作已取消");
}

function invalidationTargets(
  kind: string
): readonly OperationInvalidationTarget[] {
  if (kind === "component.download" || kind === "component.install") {
    return Object.freeze(["components"]);
  }
  if (kind === "dependency.download" || kind === "dependency.launch-installer") {
    return Object.freeze(["dependencies"]);
  }
  if (kind === "software.uninstall") {
    return Object.freeze(["software"]);
  }
  if (kind === "migration.execute" || kind === "migration.restore") {
    return Object.freeze(["migration-records", "migration-sessions"]);
  }
  if (kind === "discovery.start") {
    return Object.freeze(["migration-sessions"]);
  }
  return Object.freeze([]);
}
