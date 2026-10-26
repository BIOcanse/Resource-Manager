import { MAX_UNSUBSCRIBED_SOURCE_ENTRIES } from "../source/sourceRetention.ts";
import type { SourceSnapshot } from "../source/SourceSnapshot.ts";
import type { ResponseDecoder } from "../request/ResponseDecoder.ts";
import {
  type BackendSubscriptionChannel,
  type BackendSubscriptionChannelLease
} from "./BackendSubscriptionChannel.ts";

export interface PushValueSourceFamily<TQuery, TValue> {
  readonly key: string;
  readSnapshot(query: TQuery): SourceSnapshot<TValue>;
  subscribeSnapshot(
    query: TQuery,
    intervalMs: number,
    listener: (value: SourceSnapshot<TValue>) => void
  ): () => void;
}

export interface PushValueSource<TValue> {
  readonly key: string;
  readSnapshot(): SourceSnapshot<TValue>;
  subscribeSnapshot(
    intervalMs: number,
    listener: (value: SourceSnapshot<TValue>) => void
  ): () => void;
}

export interface CurrentValueSource<TValue> {
  readonly key: string;
  readSnapshot(): SourceSnapshot<TValue>;
  subscribeSnapshot(listener: (value: SourceSnapshot<TValue>) => void): () => void;
}

export interface PushValueSourceFamilyOptions<TQuery, TValue> {
  readonly key: string;
  readonly channel: BackendSubscriptionChannel;
  readonly buildUrl: (query: TQuery, intervalMs: number) => string;
  readonly canonicalizeQuery?: (query: TQuery) => string;
  readonly decoder: ResponseDecoder<TValue>;
}

export interface PushValueSourceOptions<TValue> {
  readonly key: string;
  readonly channel: BackendSubscriptionChannel;
  readonly buildUrl: (intervalMs: number) => string;
  readonly decoder: ResponseDecoder<TValue>;
}

export interface CurrentValueSourceOptions<TValue> {
  readonly key: string;
  readonly channel: BackendSubscriptionChannel;
  readonly buildUrl: () => string;
  readonly decoder: ResponseDecoder<TValue>;
}

interface PushListener<TValue> {
  readonly intervalMs: number;
  readonly publish: (value: SourceSnapshot<TValue>) => void;
}

interface PushEntry<TQuery, TValue> {
  readonly canonicalQuery: string;
  readonly query: TQuery;
  readonly listeners: Map<number, PushListener<TValue>>;
  snapshot: SourceSnapshot<TValue>;
  channelLease: BackendSubscriptionChannelLease | null;
  effectiveIntervalMs: number | null;
}

export class BackendPushValueSourceFamily<TQuery, TValue>
implements PushValueSourceFamily<TQuery, TValue> {
  readonly key: string;
  private readonly channel: BackendSubscriptionChannel;
  private readonly buildUrl: (query: TQuery, intervalMs: number) => string;
  private readonly canonicalizeQuery: (query: TQuery) => string;
  private readonly decoder: ResponseDecoder<TValue>;
  private readonly entries = new Map<string, PushEntry<TQuery, TValue>>();
  private readonly unusedEntries = new Map<string, PushEntry<TQuery, TValue>>();
  private nextListenerId = 0;

  constructor(options: PushValueSourceFamilyOptions<TQuery, TValue>) {
    this.key = options.key;
    this.channel = options.channel;
    this.buildUrl = options.buildUrl;
    this.canonicalizeQuery = options.canonicalizeQuery
      ?? ((query) => JSON.stringify(query));
    this.decoder = options.decoder;
  }

  readSnapshot(query: TQuery): SourceSnapshot<TValue> {
    return this.entryFor(query).snapshot;
  }

  private entryFor(query: TQuery): PushEntry<TQuery, TValue> {
    const canonicalQuery = this.canonicalizeQuery(query);
    let entry = this.entries.get(canonicalQuery);
    if (!entry) {
      entry = {
        canonicalQuery, query, listeners: new Map(),
        snapshot: this.pendingSnapshot(),
        channelLease: null, effectiveIntervalMs: null
      };
      this.entries.set(canonicalQuery, entry);
    }
    if (entry.snapshot.backendEpoch !== this.channel.backendEpoch) {
      entry.snapshot = this.pendingSnapshot();
    }
    if (entry.listeners.size === 0) this.retainUnused(entry);
    return entry;
  }

  private pendingSnapshot(): SourceSnapshot<TValue> {
    return Object.freeze({
      key: this.key, status: "loading", data: null, error: null,
      backendEpoch: this.channel.backendEpoch, revision: 0,
      acceptedAttempt: null, domainRevision: null, capturedAt: null
    });
  }

  subscribeSnapshot(
    query: TQuery,
    intervalMs: number,
    listener: (value: SourceSnapshot<TValue>) => void
  ): () => void {
    const entry = this.entryFor(query);
    this.unusedEntries.delete(entry.canonicalQuery);
    const listenerId = ++this.nextListenerId;
    entry.listeners.set(listenerId, {
      intervalMs: normalizeInterval(intervalMs),
      publish: listener
    });

    try { listener(entry.snapshot); } catch {
      // Subscriber failures do not change the shared current snapshot.
    }
    this.reconfigure(entry);

    let disposed = false;
    return () => {
      if (disposed) {
        return;
      }
      disposed = true;
      entry.listeners.delete(listenerId);
      if (entry.listeners.size === 0) {
        this.releaseDemand(entry);
        return;
      }
      this.reconfigure(entry);
    };
  }

  private reconfigure(entry: PushEntry<TQuery, TValue>): void {
    if (!this.isCurrent(entry) || entry.listeners.size === 0) {
      return;
    }
    const intervalMs = Math.min(
      ...[...entry.listeners.values()].map((value) => value.intervalMs));
    if (entry.effectiveIntervalMs !== intervalMs) {
      entry.effectiveIntervalMs = intervalMs;
    }
    const path = this.buildUrl(entry.query, intervalMs);
    if (entry.channelLease) {
      entry.channelLease.updatePath(path);
    } else {
      entry.channelLease = this.channel.subscribe(
        this.key,
        path,
        this.decoder,
        (value) => this.publish(entry, value));
    }
  }

  private publish(
    entry: PushEntry<TQuery, TValue>,
    value: SourceSnapshot<TValue>
  ): void {
    if (!this.isCurrent(entry)) {
      return;
    }
    // Reopening transport is demand, not a new value. Session changes still fence it.
    if (value.status === "loading" && entry.snapshot.revision > 0
      && value.backendEpoch === entry.snapshot.backendEpoch) {
      return;
    }
    const snapshot = Object.freeze({
      ...value,
      revision: (entry.snapshot?.revision ?? 0) + 1
    });
    entry.snapshot = snapshot;
    for (const listener of entry.listeners.values()) {
      try {
        listener.publish(snapshot);
      } catch {
        // A consumer cannot replace or terminate the shared transport owner.
      }
    }
  }

  private releaseDemand(entry: PushEntry<TQuery, TValue>): void {
    if (!this.isCurrent(entry)) {
      return;
    }
    entry.channelLease?.dispose();
    entry.channelLease = null;
    entry.effectiveIntervalMs = null;
    this.retainUnused(entry);
  }

  private retainUnused(entry: PushEntry<TQuery, TValue>): void {
    this.unusedEntries.delete(entry.canonicalQuery);
    this.unusedEntries.set(entry.canonicalQuery, entry);
    while (this.unusedEntries.size > MAX_UNSUBSCRIBED_SOURCE_ENTRIES) {
      const oldest = this.unusedEntries.keys().next().value!;
      this.unusedEntries.delete(oldest);
      this.entries.delete(oldest);
    }
  }

  private isCurrent(entry: PushEntry<TQuery, TValue>): boolean {
    return this.entries.get(entry.canonicalQuery) === entry;
  }

}

export class BackendPushValueSource<TValue>
implements PushValueSource<TValue> {
  readonly key: string;
  private readonly family: BackendPushValueSourceFamily<null, TValue>;

  constructor(options: PushValueSourceOptions<TValue>) {
    this.key = options.key;
    this.family = new BackendPushValueSourceFamily<null, TValue>({
      key: options.key,
      channel: options.channel,
      buildUrl: (_query, intervalMs) => options.buildUrl(intervalMs),
      canonicalizeQuery: () => "current",
      decoder: options.decoder
    });
  }

  readSnapshot(): SourceSnapshot<TValue> {
    return this.family.readSnapshot(null);
  }

  subscribeSnapshot(
    intervalMs: number,
    listener: (value: SourceSnapshot<TValue>) => void
  ): () => void {
    return this.family.subscribeSnapshot(null, intervalMs, listener);
  }
}

export class BackendCurrentValueSource<TValue>
implements CurrentValueSource<TValue> {
  readonly key: string;
  private readonly source: BackendPushValueSource<TValue>;

  constructor(options: CurrentValueSourceOptions<TValue>) {
    this.key = options.key;
    this.source = new BackendPushValueSource<TValue>({
      key: options.key,
      channel: options.channel,
      buildUrl: options.buildUrl,
      decoder: options.decoder
    });
  }

  readSnapshot(): SourceSnapshot<TValue> {
    return this.source.readSnapshot();
  }

  subscribeSnapshot(listener: (value: SourceSnapshot<TValue>) => void): () => void {
    return this.source.subscribeSnapshot(1, listener);
  }
}

function normalizeInterval(intervalMs: number): number {
  return Math.max(1, Math.ceil(intervalMs));
}
