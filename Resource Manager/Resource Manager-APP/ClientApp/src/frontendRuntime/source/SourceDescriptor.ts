import type {
  SourceDomainRevision,
  SourceSnapshot
} from "./SourceSnapshot.ts";

export interface SourceDescriptor<T> {
  readonly key: string;
  readonly load: (signal: AbortSignal) => Promise<T>;
  readonly retryPolicy?: SourceRetryPolicy;
  readonly mergeValue?: (previous: T | null, incoming: T) => T;
  readonly selectDomainRevision?: (value: T) => SourceDomainRevision;
  readonly selectCapturedAt?: (value: T) => string | null;
  readonly retainLastGood?: boolean;
  readonly canRetainStale?: (error: unknown) => boolean;
}

export interface CanonicalSourceQuery<TQuery> {
  readonly key: string;
  readonly value: TQuery;
}

export interface SourceFamilyDescriptor<TQuery, TValue> {
  readonly key: string;
  readonly canonicalizeQuery?: (query: TQuery) => CanonicalSourceQuery<TQuery>;
  readonly load: (query: TQuery, signal: AbortSignal) => Promise<TValue>;
  readonly retryPolicy?: SourceRetryPolicy;
  readonly mergeValue?: (previous: TValue | null, incoming: TValue) => TValue;
  readonly selectDomainRevision?: (value: TValue) => SourceDomainRevision;
  readonly selectCapturedAt?: (value: TValue) => string | null;
  readonly retainLastGood?: boolean;
  readonly canRetainStale?: (error: unknown) => boolean;
}

export interface SourceRetryPolicy {
  readonly initialDelayMs: number;
  readonly maximumDelayMs: number;
  readonly multiplier?: number;
  readonly jitterRatio?: number;
}

export interface SourceDemand {
  readonly active: boolean;
  readonly refreshIntervalMs?: number | null;
}

export interface SourceLease<T> {
  readonly key: string;
  readonly snapshot: SourceSnapshot<T>;
  subscribe(listener: (snapshot: SourceSnapshot<T>) => void): () => void;
  setDemand(demand: SourceDemand): void;
  refresh(): Promise<SourceSnapshot<T>>;
  acceptAuthoritative(value: T): SourceSnapshot<T>;
  release(): void;
}

export interface SourceHandle<T> {
  readonly key: string;
  acquire(initialDemand?: SourceDemand): SourceLease<T>;
}

export interface SourceFamily<TQuery, TValue> {
  readonly key: string;
  acquire(query: TQuery, initialDemand?: SourceDemand): SourceLease<TValue>;
}
