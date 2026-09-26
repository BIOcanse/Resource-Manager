import type { ResponseDecoder } from "../request/ResponseDecoder.ts";
import type { BackendSessionOwner } from "../session/BackendSessionOwner.ts";

export interface BackendSubscriptionChannelLease {
  readonly id: string;
  updatePath(path: string): void;
  dispose(): void;
}

export interface BackendSubscriptionChannelOptions {
  readonly backendSession: BackendSessionOwner;
  readonly fetch?: typeof globalThis.fetch;
  readonly reconnectDelayMs?: number;
}

interface SubscriptionEntry {
  readonly id: string;
  path: string;
  readonly decode: (value: unknown) => unknown;
  readonly publish: (value: unknown) => void;
}

interface ActiveSubscription {
  readonly entry: SubscriptionEntry;
  readonly path: string;
}

interface SubscriptionRequestItem {
  readonly id: string;
  readonly path: string;
}

interface ConsumeOutcome {
  readonly terminal: boolean;
  readonly delivered: boolean;
}

interface SubscriptionFrame {
  readonly subscriptionId: string;
  readonly value: unknown;
}

const subscriptionEndpoint = "/api/subscriptions/stream";

export class BackendSubscriptionChannel {
  private readonly backendSession: BackendSessionOwner;
  private readonly fetch: typeof globalThis.fetch;
  private readonly reconnectDelayMs: number;
  private readonly entries = new Map<string, SubscriptionEntry>();
  private readonly unsubscribeSession: () => void;
  private activeController: AbortController | null = null;
  private activeTask: Promise<void> | null = null;
  private activeSessionSignal: AbortSignal | null = null;
  private activeSignature: string | null = null;
  private synchronizationScheduled = false;
  private synchronizationRunning = false;
  private synchronizationRequested = false;
  private disposed = false;
  private nextSubscriptionId = 0;

  constructor(options: BackendSubscriptionChannelOptions) {
    this.backendSession = options.backendSession;
    this.fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.reconnectDelayMs = Math.max(1, options.reconnectDelayMs ?? 1_000);
    this.unsubscribeSession = this.backendSession.subscribe(
      () => this.scheduleSynchronization());
  }

  subscribe<TValue>(
    key: string,
    path: string,
    decoder: ResponseDecoder<TValue>,
    listener: (value: TValue) => void
  ): BackendSubscriptionChannelLease {
    this.throwIfDisposed();
    const id = `${normalizeKey(key)}:${++this.nextSubscriptionId}`;
    const entry: SubscriptionEntry = {
      id,
      path,
      decode: (value) => decoder.decode(value),
      publish: (value) => listener(value as TValue)
    };
    this.entries.set(id, entry);
    this.scheduleSynchronization();

    let disposed = false;
    return {
      id,
      updatePath: (nextPath) => {
        if (disposed || this.entries.get(id) !== entry) {
          return;
        }
        if (entry.path === nextPath) {
          return;
        }
        entry.path = nextPath;
        this.scheduleSynchronization();
      },
      dispose: () => {
        if (disposed) {
          return;
        }
        disposed = true;
        if (this.entries.get(id) === entry) {
          this.entries.delete(id);
          this.scheduleSynchronization();
        }
      }
    };
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    this.unsubscribeSession();
    this.entries.clear();
    this.stopActiveStream();
  }

  private scheduleSynchronization(): void {
    if (this.disposed) {
      return;
    }
    this.synchronizationRequested = true;
    if (this.synchronizationScheduled || this.synchronizationRunning) {
      return;
    }
    this.synchronizationScheduled = true;
    queueMicrotask(() => {
      this.synchronizationScheduled = false;
      void this.runSynchronizationLoop();
    });
  }

  private async runSynchronizationLoop(): Promise<void> {
    if (this.synchronizationRunning || this.disposed) {
      return;
    }
    this.synchronizationRunning = true;
    try {
      while (this.synchronizationRequested && !this.disposed) {
        this.synchronizationRequested = false;
        await this.synchronizeOnce();
      }
    } finally {
      this.synchronizationRunning = false;
      if (this.synchronizationRequested && !this.disposed) {
        this.scheduleSynchronization();
      }
    }
  }

  private async synchronizeOnce(): Promise<void> {
    const items = [...this.entries.values()]
      .map((entry): SubscriptionRequestItem => ({
        id: entry.id,
        path: entry.path
      }))
      .sort((left, right) => left.id.localeCompare(right.id));
    const signature = JSON.stringify(items);
    const ready = this.backendSession.snapshot.status === "ready";
    const sessionSignal = ready ? this.backendSession.capture().signal : null;
    if (this.activeController
      && !this.activeController.signal.aborted
      && this.activeSessionSignal === sessionSignal
      && this.activeSignature === signature) {
      return;
    }

    await this.stopActiveStreamAndWait();
    if (this.disposed
      || this.entries.size === 0
      || this.backendSession.snapshot.status !== "ready") {
      return;
    }

    const capture = this.backendSession.capture();
    const currentItems = [...this.entries.values()]
      .map((entry): SubscriptionRequestItem => ({
        id: entry.id,
        path: entry.path
      }))
      .sort((left, right) => left.id.localeCompare(right.id));
    const currentSignature = JSON.stringify(currentItems);
    const controller = new AbortController();
    const active = new Map<string, ActiveSubscription>();
    for (const item of currentItems) {
      active.set(item.id, {
        entry: this.entries.get(item.id)!,
        path: item.path
      });
    }
    this.activeController = controller;
    this.activeSessionSignal = capture.signal;
    this.activeSignature = currentSignature;
    const abortForSessionChange = () => controller.abort();
    capture.signal.addEventListener("abort", abortForSessionChange, { once: true });

    const activeTask = this.consumeUntilStopped(currentItems, active, controller)
      .catch(() => undefined)
      .finally(() => {
        capture.signal.removeEventListener("abort", abortForSessionChange);
        if (this.activeController === controller) {
          this.activeController = null;
          this.activeSessionSignal = null;
          this.activeSignature = null;
          this.activeTask = null;
        }
      });
    this.activeTask = activeTask;
  }

  private stopActiveStream(): void {
    this.activeController?.abort();
  }

  private async stopActiveStreamAndWait(): Promise<void> {
    const controller = this.activeController;
    const task = this.activeTask;
    controller?.abort();
    if (task) {
      await task;
      await yieldForTransportClose();
    }
    if (this.activeController === controller) {
      this.activeController = null;
      this.activeSessionSignal = null;
      this.activeSignature = null;
      this.activeTask = null;
    }
  }

  private async consumeUntilStopped(
    items: readonly SubscriptionRequestItem[],
    active: ReadonlyMap<string, ActiveSubscription>,
    controller: AbortController
  ): Promise<void> {
    let retryDelayMs = this.reconnectDelayMs;
    const maximumRetryDelayMs = Math.max(30_000, retryDelayMs);
    while (!controller.signal.aborted) {
      try {
        const outcome = await this.consume(items, active, controller);
        if (outcome.terminal) {
          await waitUntilAborted(controller.signal);
          return;
        }
        if (outcome.delivered) {
          retryDelayMs = this.reconnectDelayMs;
        }
      } catch {
        if (controller.signal.aborted) {
          return;
        }
      }
      if (controller.signal.aborted) {
        return;
      }
      await waitForReconnect(retryDelayMs, controller.signal);
      retryDelayMs = Math.min(
        maximumRetryDelayMs,
        Math.max(this.reconnectDelayMs, retryDelayMs * 2));
    }
  }

  private async consume(
    items: readonly SubscriptionRequestItem[],
    active: ReadonlyMap<string, ActiveSubscription>,
    controller: AbortController
  ): Promise<ConsumeOutcome> {
    const response = await this.fetch(subscriptionEndpoint, {
      method: "POST",
      cache: "no-store",
      signal: controller.signal,
      headers: {
        Accept: "application/x-ndjson",
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        version: 1,
        subscriptions: items
      })
    });
    if (!response.ok) {
      await response.body?.cancel().catch(() => undefined);
      return {
        terminal: !isRetryableStatus(response.status),
        delivered: false
      };
    }
    if (!response.body) {
      return { terminal: false, delivered: false };
    }

    const reader = response.body.getReader();
    const textDecoder = new TextDecoder();
    let pending = "";
    let completed = false;
    let delivered = false;
    try {
      while (!controller.signal.aborted) {
        const chunk = await reader.read();
        if (chunk.done) {
          pending += textDecoder.decode();
          delivered = this.publishLines(
            pending,
            active,
            controller,
            true) || delivered;
          completed = true;
          return { terminal: false, delivered };
        }
        pending += textDecoder.decode(chunk.value, { stream: true });
        const lastNewLine = pending.lastIndexOf("\n");
        if (lastNewLine < 0) {
          continue;
        }
        const complete = pending.slice(0, lastNewLine + 1);
        pending = pending.slice(lastNewLine + 1);
        delivered = this.publishLines(
          complete,
          active,
          controller,
          false) || delivered;
      }
      return { terminal: true, delivered };
    } finally {
      if (!completed) {
        await reader.cancel().catch(() => undefined);
      }
      reader.releaseLock();
    }
  }

  private publishLines(
    text: string,
    active: ReadonlyMap<string, ActiveSubscription>,
    controller: AbortController,
    includeTrailingLine: boolean
  ): boolean {
    const lines = text.split("\n");
    const count = includeTrailingLine ? lines.length : lines.length - 1;
    let delivered = false;
    for (let index = 0;
      index < count && !controller.signal.aborted;
      index += 1) {
      const line = lines[index].trim();
      if (!line) {
        continue;
      }
      const frame = decodeFrame(JSON.parse(line));
      const subscription = active.get(frame.subscriptionId);
      if (!subscription
        || this.activeController !== controller
        || this.entries.get(frame.subscriptionId) !== subscription.entry
        || subscription.entry.path !== subscription.path) {
        continue;
      }
      try {
        const value = subscription.entry.decode(frame.value);
        subscription.entry.publish(value);
        delivered = true;
      } catch {
        // One item's decoder or listener cannot discard another item's value.
      }
    }
    return delivered;
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error("BackendSubscriptionChannel is disposed.");
    }
  }
}

function yieldForTransportClose(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function decodeFrame(value: unknown): SubscriptionFrame {
  if (!isRecord(value)
    || typeof value.subscriptionId !== "string"
    || !value.subscriptionId
    || !Object.prototype.hasOwnProperty.call(value, "value")) {
    throw new Error("The backend subscription frame is invalid.");
  }
  return {
    subscriptionId: value.subscriptionId,
    value: value.value
  };
}

function normalizeKey(value: string): string {
  const key = value.trim();
  if (!key) {
    throw new Error("A backend subscription key is required.");
  }
  return key;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isRetryableStatus(status: number): boolean {
  return status === 408
    || status === 425
    || status === 429
    || status >= 500;
}

function waitUntilAborted(signal: AbortSignal): Promise<void> {
  if (signal.aborted) {
    return Promise.resolve();
  }
  return new Promise((resolve) => {
    signal.addEventListener("abort", () => resolve(), { once: true });
  });
}

function waitForReconnect(delayMs: number, signal: AbortSignal): Promise<void> {
  if (signal.aborted) {
    return Promise.resolve();
  }
  return new Promise((resolve) => {
    const timer = setTimeout(finish, delayMs);
    signal.addEventListener("abort", finish, { once: true });
    function finish() {
      clearTimeout(timer);
      signal.removeEventListener("abort", finish);
      resolve();
    }
  });
}
