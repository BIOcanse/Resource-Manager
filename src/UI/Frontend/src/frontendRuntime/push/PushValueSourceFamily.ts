import type { ResponseDecoder } from "../request/ResponseDecoder.ts";
import {
  type BackendSubscriptionChannel,
  type BackendSubscriptionChannelLease
} from "./BackendSubscriptionChannel.ts";

export interface PushValueSourceFamily<TQuery, TValue> {
  readonly key: string;
  subscribe(
    query: TQuery,
    intervalMs: number,
    listener: (value: TValue) => void
  ): () => void;
}

export interface PushValueSource<TValue> {
  readonly key: string;
  subscribe(
    intervalMs: number,
    listener: (value: TValue) => void
  ): () => void;
}

export interface CurrentValueSource<TValue> {
  readonly key: string;
  subscribe(listener: (value: TValue) => void): () => void;
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
  readonly publish: (value: TValue) => void;
}

interface PushEntry<TQuery, TValue> {
  readonly canonicalQuery: string;
  readonly query: TQuery;
  readonly listeners: Map<number, PushListener<TValue>>;
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
  private nextListenerId = 0;

  constructor(options: PushValueSourceFamilyOptions<TQuery, TValue>) {
    this.key = options.key;
    this.channel = options.channel;
    this.buildUrl = options.buildUrl;
    this.canonicalizeQuery = options.canonicalizeQuery
      ?? ((query) => JSON.stringify(query));
    this.decoder = options.decoder;
  }

  subscribe(
    query: TQuery,
    intervalMs: number,
    listener: (value: TValue) => void
  ): () => void {
    const canonicalQuery = this.canonicalizeQuery(query);
    let entry = this.entries.get(canonicalQuery);
    if (!entry) {
      entry = {
        canonicalQuery,
        query,
        listeners: new Map(),
        channelLease: null,
        effectiveIntervalMs: null
      };
      this.entries.set(canonicalQuery, entry);
    }

    const listenerId = ++this.nextListenerId;
    entry.listeners.set(listenerId, {
      intervalMs: normalizeInterval(intervalMs),
      publish: listener
    });

    this.reconfigure(entry);

    let disposed = false;
    return () => {
      if (disposed) {
        return;
      }
      disposed = true;
      entry!.listeners.delete(listenerId);
      if (entry!.listeners.size === 0) {
        this.disposeEntry(entry!);
        return;
      }
      this.reconfigure(entry!);
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
        (value) => this.publish(entry!, value));
    }
  }

  private publish(
    entry: PushEntry<TQuery, TValue>,
    value: TValue
  ): void {
    if (!this.isCurrent(entry)) {
      return;
    }
    for (const listener of entry.listeners.values()) {
      try {
        listener.publish(value);
      } catch {
        // A consumer cannot replace or terminate the shared transport owner.
      }
    }
  }

  private disposeEntry(entry: PushEntry<TQuery, TValue>): void {
    if (!this.isCurrent(entry)) {
      return;
    }
    this.entries.delete(entry.canonicalQuery);
    entry.channelLease?.dispose();
    entry.channelLease = null;
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

  subscribe(
    intervalMs: number,
    listener: (value: TValue) => void
  ): () => void {
    return this.family.subscribe(null, intervalMs, listener);
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

  subscribe(listener: (value: TValue) => void): () => void {
    return this.source.subscribe(1, listener);
  }
}

function normalizeInterval(intervalMs: number): number {
  return Math.max(1, Math.ceil(intervalMs));
}
