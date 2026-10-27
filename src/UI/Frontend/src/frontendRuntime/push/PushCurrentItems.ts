import type { SourceSnapshot } from "../source/SourceSnapshot.ts";

export interface PushValueItem<TValue> {
  readonly key: string;
  readonly capturedAt: string | null;
  /** A typed fragment of the domain value, containing one atomic item. */
  readonly value: TValue;
}

export interface PushValueItemPolicy<TQuery, TValue> {
  /** True only for backend collections whose omitted members mean empty/removal. */
  absentMeansEmpty(query: TQuery, key: string): boolean;
  split(query: TQuery, value: TValue): readonly PushValueItem<TValue>[];
  includes(query: TQuery, key: string): boolean;
  /** Some domains (dashboard metrics) read all current slots while demanding only visible slots. */
  includesInProjection?(query: TQuery, key: string): boolean;
  isPending?(query: TQuery, answeredKeys: ReadonlySet<string>, readItem: (key: string) => TValue | null): boolean;
  combine(query: TQuery, values: readonly TValue[], latest: TValue): TValue;
}

interface CurrentItem<TValue> {
  readonly snapshot: SourceSnapshot<TValue>;
  readonly capturedAt: string | null;
  readonly envelope: TValue | null;
}

/** Family-owned items. Query snapshots are projections, never competing caches. */
export class PushCurrentItems<TQuery, TValue> {
  private readonly items = new Map<string, CurrentItem<TValue>>();

  constructor(private readonly policy: PushValueItemPolicy<TQuery, TValue>) {}

  clear(): void { this.items.clear(); }

  accept(query: TQuery, snapshot: SourceSnapshot<TValue>): ReadonlySet<string> {
    const incoming = new Map(snapshot.data === null ? [] : this.policy.split(query, snapshot.data)
      .map(item => [item.key, item] as const));
    // A missing placeholder is not a backend value. Only complete-set domains
    // clear omitted members; an explicit source failure still replaces its items.
    const keys = new Set([...incoming.keys(), ...[...this.items.keys()]
      .filter(key => this.policy.includes(query, key)
        && (snapshot.data === null || this.policy.absentMeansEmpty(query, key)))]);
    const changed = new Set<string>();
    for (const key of keys) {
      const item = incoming.get(key);
      const capturedAt = item?.capturedAt ?? snapshot.capturedAt;
      const previous = this.items.get(key);
      if (capturedAt !== null && previous?.capturedAt !== null && previous?.capturedAt !== undefined
        && compareCaptureTime(capturedAt, previous.capturedAt) < 0) continue;
      this.items.set(key, {
        capturedAt: capturedAt ?? previous?.capturedAt ?? null,
        envelope: snapshot.data,
        snapshot: Object.freeze({ ...snapshot, data: item?.value ?? null })
      });
      changed.add(key);
    }
    return changed;
  }

  project(query: TQuery, fallback: SourceSnapshot<TValue>): SourceSnapshot<TValue> {
    const pending = this.policy.isPending?.(query, new Set(this.items.keys()),
      key => this.items.get(key)?.snapshot.data ?? null) ?? false;
    const selected = [...this.items].filter(([key]) => (this.policy.includesInProjection ?? this.policy.includes)(query, key))
      .map(([, item]) => item);
    if (selected.length === 0) return pending && fallback.status === "ready"
      ? Object.freeze({ ...fallback, status: "loading" }) : fallback;
    const latest = selected.reduce((left, right) => left.snapshot.revision > right.snapshot.revision ? left : right);
    const values = selected.flatMap(item => item.snapshot.data === null ? [] : [item.snapshot.data]);
    const failed = [...this.items].filter(([key]) => this.policy.includes(query, key)).map(([, item]) => item.snapshot)
      .find(item => item.status === "error" || item.status === "unavailable" || item.status === "disposed");
    const envelope = latest.envelope ?? values.at(-1);
    return Object.freeze({
      ...latest.snapshot,
      status: failed?.status ?? (pending ? "loading" : "ready"),
      error: failed?.error ?? null,
      data: envelope ? this.policy.combine(query, values, envelope) : null
    });
  }

  intersects(query: TQuery, changed: ReadonlySet<string>): boolean {
    return [...changed].some(key => (this.policy.includesInProjection ?? this.policy.includes)(query, key));
  }

  retain(queries: readonly TQuery[]): void {
    for (const key of this.items.keys()) {
      if (!queries.some(query => this.policy.includes(query, key))) this.items.delete(key);
    }
  }
}

function compareCaptureTime(left: string, right: string): number {
  const milliseconds = Date.parse(left) - Date.parse(right);
  if (milliseconds !== 0) return milliseconds;
  // Backend timestamps carry sub-millisecond ticks; Date.parse alone loses them.
  const fraction = (value: string) => (value.match(/\.(\d+)(?:Z|[+-]\d{2}:\d{2})$/)?.[1] ?? "")
    .slice(3).padEnd(9, "0");
  return fraction(left).localeCompare(fraction(right));
}
