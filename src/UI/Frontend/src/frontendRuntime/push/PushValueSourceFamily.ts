import { MAX_UNSUBSCRIBED_SOURCE_ENTRIES } from "../source/sourceRetention.ts";
import type { SourceSnapshot } from "../source/SourceSnapshot.ts";
import type { ResponseDecoder } from "../request/ResponseDecoder.ts";
import { PushCurrentItems, type PushValueItemPolicy } from "./PushCurrentItems.ts";
import {
  type BackendSubscriptionChannel,
  type BackendSubscriptionChannelLease
} from "./BackendSubscriptionChannel.ts";

export interface PushValueSourceFamily<TQuery, TValue> {
  readonly key: string;
  /**
   * The family member for one query. The query is canonicalized once here; reading or subscribing through the
   * returned source does not canonicalize it again, so a consumer holding one demand pays for it once per change.
   */
  select(query: TQuery): PushValueSource<TValue>;
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
  readonly currentItems?: PushValueItemPolicy<TQuery, TValue>;
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
  private readonly currentItems: PushCurrentItems<TQuery, TValue> | null;
  private backendEpoch: string | null;
  private publicationRevision = 0;

  constructor(options: PushValueSourceFamilyOptions<TQuery, TValue>) {
    this.key = options.key;
    this.channel = options.channel;
    this.buildUrl = options.buildUrl;
    this.canonicalizeQuery = options.canonicalizeQuery
      ?? ((query) => JSON.stringify(query));
    this.decoder = options.decoder;
    this.currentItems = options.currentItems ? new PushCurrentItems(options.currentItems) : null;
    this.backendEpoch = this.channel.backendEpoch;
  }

  select(query: TQuery): PushValueSource<TValue> {
    const canonicalQuery = this.canonicalizeQuery(query);
    return {
      key: this.key,
      readSnapshot: () => this.entryFor(canonicalQuery, query).snapshot,
      subscribeSnapshot: (intervalMs, listener) => this.subscribeEntry(canonicalQuery, query, intervalMs, listener)
    };
  }

  readSnapshot(query: TQuery): SourceSnapshot<TValue> {
    return this.select(query).readSnapshot();
  }

  subscribeSnapshot(
    query: TQuery,
    intervalMs: number,
    listener: (value: SourceSnapshot<TValue>) => void
  ): () => void {
    return this.select(query).subscribeSnapshot(intervalMs, listener);
  }

  private entryFor(canonicalQuery: string, query: TQuery): PushEntry<TQuery, TValue> {
    this.fenceEpoch();
    let entry = this.entries.get(canonicalQuery);
    if (!entry) {
      entry = {
        canonicalQuery, query, listeners: new Map(),
        snapshot: this.pendingSnapshot(),
        channelLease: null, effectiveIntervalMs: null
      };
      this.entries.set(canonicalQuery, entry);
      entry.snapshot = this.currentItems?.project(query, entry.snapshot) ?? entry.snapshot;
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

  private subscribeEntry(
    canonicalQuery: string,
    query: TQuery,
    intervalMs: number,
    listener: (value: SourceSnapshot<TValue>) => void
  ): () => void {
    const entry = this.entryFor(canonicalQuery, query);
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
    this.fenceEpoch();
    if (value.backendEpoch !== this.channel.backendEpoch) return;
    // Reopening transport is demand, not a new value. Session changes still fence it.
    if (value.status === "loading" && entry.snapshot.revision > 0
      && value.backendEpoch === entry.snapshot.backendEpoch) {
      return;
    }
    const snapshot = Object.freeze({
      ...value,
      revision: ++this.publicationRevision
    });
    if (this.currentItems && value.status !== "loading") {
      const changed = this.currentItems.accept(entry.query, snapshot);
      // Update all projections before notifying any consumer (including reentrant reads).
      const affected = [...this.entries.values()].filter(candidate => candidate === entry
        || this.currentItems!.intersects(candidate.query, changed));
      for (const candidate of affected) {
        candidate.snapshot = this.currentItems.project(candidate.query, candidate === entry ? snapshot : candidate.snapshot);
      }
      for (const candidate of affected) this.notify(candidate);
      return;
    }
    entry.snapshot = snapshot;
    this.notify(entry);
  }

  private notify(entry: PushEntry<TQuery, TValue>): void {
    for (const listener of entry.listeners.values()) {
      try {
        listener.publish(entry.snapshot);
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
      this.currentItems?.retain([...this.entries.values()].map(candidate => candidate.query));
    }
  }

  private isCurrent(entry: PushEntry<TQuery, TValue>): boolean {
    return this.entries.get(entry.canonicalQuery) === entry;
  }

  private fenceEpoch(): void {
    if (this.backendEpoch === this.channel.backendEpoch) return;
    this.backendEpoch = this.channel.backendEpoch;
    this.currentItems?.clear();
    for (const entry of this.entries.values()) entry.snapshot = this.pendingSnapshot();
  }

}

export class BackendPushValueSource<TValue>
implements PushValueSource<TValue> {
  readonly key: string;
  private readonly current: PushValueSource<TValue>;

  constructor(options: PushValueSourceOptions<TValue>) {
    this.key = options.key;
    this.current = new BackendPushValueSourceFamily<null, TValue>({
      key: options.key,
      channel: options.channel,
      buildUrl: (_query, intervalMs) => options.buildUrl(intervalMs),
      canonicalizeQuery: () => "current",
      decoder: options.decoder
    }).select(null);
  }

  readSnapshot(): SourceSnapshot<TValue> {
    return this.current.readSnapshot();
  }

  subscribeSnapshot(
    intervalMs: number,
    listener: (value: SourceSnapshot<TValue>) => void
  ): () => void {
    return this.current.subscribeSnapshot(intervalMs, listener);
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
