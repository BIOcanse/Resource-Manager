import type { SourceSnapshot, SourceStatus } from "../source/SourceSnapshot.ts";
import type { ResponseDecoder } from "../request/ResponseDecoder.ts";
import type { BackendSessionOwner } from "../session/BackendSessionOwner.ts";

/** 订阅项里的一个设备信息字段：设备地址（deviceIdentity）和字段名，与后端 FieldReference 同形。 */
export interface SubscriptionFieldReference {
  readonly deviceIdentity: string;
  readonly fieldName: string;
}

/**
 * 一个订阅项要什么：路径，和后端订阅项的字段选择 fields。fields 为 null 时请求里不写 fields，
 * 后端按这个路径的整份内容发（例如 /api/device-topology/fields/subscribe 不选字段时发全部信息字段）。
 */
export interface SubscriptionAddress {
  readonly path: string;
  readonly fields: readonly SubscriptionFieldReference[] | null;
}

export interface BackendSubscriptionChannelLease {
  readonly id: string;
  update(address: SubscriptionAddress): void;
  dispose(): void;
}

export interface BackendSubscriptionChannelOptions {
  readonly backendSession: BackendSessionOwner;
  readonly fetch?: typeof globalThis.fetch;
  readonly reconnectDelayMs?: number;
}

interface SubscriptionEntry {
  readonly id: string;
  address: SubscriptionAddress;
  /** address 的内容比较键：同一个地址再写一次不算变化，在途的帧按它判断是否还属于这一项。 */
  addressKey: string;
  readonly decode: (value: unknown) => unknown;
  readonly key: string;
  revision: number;
  readonly publish: (value: SourceSnapshot<unknown>) => void;
}

interface ActiveSubscription {
  readonly entry: SubscriptionEntry;
  readonly addressKey: string;
}

interface SubscriptionRequestItem {
  readonly id: string;
  readonly path: string;
  readonly fields?: readonly SubscriptionFieldReference[];
}

interface ConsumeOutcome {
  readonly terminal: boolean;
  readonly delivered: boolean;
  readonly failure?: unknown;
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
      () => {
        const sameSession = this.backendSession.snapshot.status === "ready"
          && this.activeSessionSignal === this.backendSession.capture().signal;
        if (!sameSession) {
          for (const entry of this.entries.values()) {
            this.publishSessionState(entry);
          }
        }
        this.scheduleSynchronization();
      });
  }

  /** Detached source entries use this fence before replaying their current value. */
  get backendEpoch(): string | null {
    const session = this.backendSession.snapshot;
    return session.status === "ready" ? session.session?.epoch ?? null : null;
  }

  subscribe<TValue>(
    key: string,
    address: SubscriptionAddress,
    decoder: ResponseDecoder<TValue>,
    listener: (value: SourceSnapshot<TValue>) => void
  ): BackendSubscriptionChannelLease {
    this.throwIfDisposed();
    const id = `${normalizeKey(key)}:${++this.nextSubscriptionId}`;
    const entry: SubscriptionEntry = {
      id,
      key,
      revision: 0,
      address,
      addressKey: subscriptionAddressKey(address),
      decode: (value) => decoder.decode(value),
      publish: (value) => listener(value as SourceSnapshot<TValue>)
    };
    this.entries.set(id, entry);
    this.publishSessionState(entry);
    this.scheduleSynchronization();

    let disposed = false;
    return {
      id,
      update: (nextAddress) => {
        if (disposed || this.entries.get(id) !== entry) {
          return;
        }
        const nextKey = subscriptionAddressKey(nextAddress);
        if (entry.addressKey === nextKey) {
          return;
        }
        entry.address = nextAddress;
        entry.addressKey = nextKey;
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

  /**
   * 授权变了（界面的默认密钥被启用、重新生成，或用户显式重试）：结束当前的流，按当前订阅重新连接，集合不变也重连。
   * 401/403 本身仍是终止、不自动重试；后端不会通知授权已恢复，只有知道授权变了的调用方才调这里。
   * 重连是需求、不是新值：各订阅保持现有快照，直到新流送来值（和传输重开一样，见 PushValueSourceFamily）。
   */
  reconnect(): void {
    this.throwIfDisposed();
    this.stopActiveStream();
    this.scheduleSynchronization();
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    this.unsubscribeSession();
    for (const entry of this.entries.values()) {
      this.publishState(entry, "disposed");
    }
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
    const items = this.requestItems();
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
    const currentItems = this.requestItems();
    const currentSignature = JSON.stringify(currentItems);
    const controller = new AbortController();
    const active = new Map<string, ActiveSubscription>();
    for (const item of currentItems) {
      const entry = this.entries.get(item.id)!;
      active.set(item.id, { entry, addressKey: entry.addressKey });
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

  /** 请求体里的订阅项，按 id 排序；没有字段选择的项不写 fields。 */
  private requestItems(): SubscriptionRequestItem[] {
    return [...this.entries.values()]
      .map((entry): SubscriptionRequestItem => entry.address.fields === null
        ? { id: entry.id, path: entry.address.path }
        : { id: entry.id, path: entry.address.path, fields: entry.address.fields })
      .sort((left, right) => left.id.localeCompare(right.id));
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
        if (!controller.signal.aborted) {
          this.publishFailure(active, controller, outcome.failure ?? new Error("The backend subscription stream ended."));
        }
        if (outcome.terminal) {
          await waitUntilAborted(controller.signal);
          return;
        }
        if (outcome.delivered) {
          retryDelayMs = this.reconnectDelayMs;
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          this.publishFailure(active, controller, error);
        }
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
        delivered: false,
        failure: new Error(`The backend subscription request failed with HTTP ${response.status}.`)
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
        || subscription.entry.addressKey !== subscription.addressKey) {
        continue;
      }
      try {
        const value = subscription.entry.decode(frame.value);
        this.publishState(subscription.entry, "ready", value);
        delivered = true;
      } catch (error) {
        this.publishState(subscription.entry, "error", null, error);
      }
    }
    return delivered;
  }

  private publishSessionState(entry: SubscriptionEntry): void {
    const session = this.backendSession.snapshot;
    this.publishState(entry, session.status === "ready" || session.status === "waiting" ? "loading" : "unavailable",
      null, session.reason ? new Error(session.reason) : null);
  }

  private publishFailure(
    active: ReadonlyMap<string, ActiveSubscription>,
    controller: AbortController,
    error: unknown
  ): void {
    for (const subscription of active.values()) {
      if (this.activeController === controller
        && this.entries.get(subscription.entry.id) === subscription.entry
        && subscription.entry.addressKey === subscription.addressKey) {
        this.publishState(subscription.entry, "error", null, error);
      }
    }
  }

  private publishState(
    entry: SubscriptionEntry,
    status: SourceStatus,
    data: unknown = null,
    error: unknown = null
  ): void {
    const snapshot: SourceSnapshot<unknown> = {
      key: entry.key, status, data, error,
      backendEpoch: this.backendSession.snapshot.session?.epoch ?? null,
      revision: ++entry.revision,
      acceptedAttempt: null, domainRevision: null,
      capturedAt: typeof data === "object" && data !== null
        && "capturedAt" in data && typeof data.capturedAt === "string"
        ? data.capturedAt : null
    };
    try {
      entry.publish(snapshot);
    } catch {
      // A listener cannot change the source result or interrupt sibling sources.
    }
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

function subscriptionAddressKey(address: SubscriptionAddress): string {
  return JSON.stringify([address.path, address.fields?.map((field) => [field.deviceIdentity, field.fieldName]) ?? null]);
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
