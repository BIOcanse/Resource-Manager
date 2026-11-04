import { SelectedReader, V1, type TrustedLayoutV1 } from "./selectedReader.ts";

/** Authorized native region references for local lifetime binding. */
export interface RegionReference {
  readonly id: number;
  readonly epoch: bigint;
  readonly layout_generation: bigint;
}

export interface RegionWake {
  readonly kind: "publication" | "loss";
  readonly sources: readonly number[];
}

/** An authorized object with adapter-proven bounds and lifetime. replace() takes ownership even on failure; release() releases only this holder. */
export interface GrantedRegion {
  readonly reference: RegionReference;
  readonly buffer: ArrayBufferLike;
  readonly descriptor: TrustedLayoutV1;
  readonly sources: readonly number[];
  readonly retryLimit: number;
  subscribe(wake: (event: RegionWake) => void): () => void;
  release(): void;
}

export type RegionChange =
  | { readonly kind: "reset" }
  | { readonly kind: "source"; readonly region: RegionReference; readonly source: number;
      readonly result: ReturnType<SelectedReader["read"]> }
  | { readonly kind: "loss"; readonly region: RegionReference; readonly source: number }
  | { readonly kind: "error"; readonly error: unknown };

export type RegionPositionValue = ReturnType<SelectedReader["positionValue"]>
  | { readonly status: "not_granted" | "version_mismatch" | "lost" };

interface HeldRegion {
  readonly grant: GrantedRegion;
  readonly reference: RegionReference;
  readonly reader: SelectedReader;
  readonly sources: ReadonlySet<number>;
  readonly positions: readonly { readonly source: number; readonly identity: number }[];
  readonly lost: Set<number>;
  readonly pending: Set<number>;
  unsubscribe: (() => void) | null;
  draining: boolean;
  scheduled: boolean;
}

interface ActiveSet {
  readonly grants: readonly GrantedRegion[];
  readonly regions: Map<number, HeldRegion>;
  readonly signal: AbortSignal;
  readonly abort: () => void;
  registering: boolean;
}

/** Binds local readers to the supplied lifetime. Replace the entire set when a grant or buffer changes, even if sequence numbers match. */
export class RegionSet {
  private active: ActiveSet | null = null;
  private disposed = false;
  private operation: object | null = null;

  constructor(private readonly publish: (change: RegionChange) => void) {}

  replace(signal: AbortSignal, grants: readonly GrantedRegion[]): void {
    // Copy the ownership list before any callbacks can mutate the input array.
    const owned = [...grants];
    const operation = {};
    this.operation = operation;
    let installed = false;
    try {
      const errors = this.detach();
      if (this.operation !== operation) return;
      this.publish({ kind: "reset" });
      if (errors.length) this.publish({ kind: "error", error: new AggregateError(errors, "Region release failed.") });
      if (this.operation !== operation) return;
      if (this.disposed) throw new Error("The region reader is disposed.");
      if (signal.aborted) return;
      const active: ActiveSet = {
        grants: owned,
        regions: new Map(),
        signal,
        abort: () => { if (this.active === active) this.clear(); },
        registering: true
      };
      this.active = active;
      installed = true;
      signal.addEventListener("abort", active.abort, { once: true });
      try {
        let grantIndex = 0;
        while (grantIndex < owned.length) {
          const grant = owned[grantIndex++];
          if (this.active !== active) return;
          const reference = Object.freeze({ ...grant.reference });
          checkReference(reference);
          if (active.regions.has(reference.id)) throw new Error("Duplicate granted region.");
          const held: HeldRegion = {
            grant,
            reference,
            reader: new SelectedReader(grant.buffer, grant.descriptor, grant.sources, grant.retryLimit),
            sources: new Set(grant.sources),
            positions: grant.descriptor.positions.map(({ source, identity }) => ({ source, identity })),
            lost: new Set(),
            pending: new Set(grant.sources),
            unsubscribe: null,
            draining: false,
            scheduled: false
          };
          active.regions.set(reference.id, held);
          const unsubscribe = grant.subscribe((event) => this.wake(active, held, event));
          // A synchronous registration callback may abort/navigate/replace.
          if (this.active !== active) {
            unsubscribe();
            return;
          }
          held.unsubscribe = unsubscribe;
        }
        if (this.active !== active) return;
        // All notification registrations precede every initial mapping read.
        active.registering = false;
        const regions = [...active.regions.values()];
        let regionIndex = 0;
        while (regionIndex < regions.length) this.drain(active, regions[regionIndex++]);
      } catch (error) {
        if (this.active === active) this.clear();
        throw error;
      }
    } finally {
      if (!installed) {
        const errors = this.releaseGrants(owned);
        if (errors.length) this.publish({ kind: "error", error: new AggregateError(errors, "Region release failed.") });
      }
    }
  }

  positionValue(reference: RegionReference, position: number): RegionPositionValue {
    const held = this.active?.regions.get(reference.id);
    if (!held) return { status: "not_granted" };
    if (!sameReference(reference, held.reference)) return { status: "version_mismatch" };
    const spec = held.positions[position];
    if (spec && (held.lost.has(spec.source)
      || (spec.identity !== V1.none && held.lost.has(held.positions[spec.identity].source)))) {
      return { status: "lost" };
    }
    return held.reader.positionValue(position);
  }

  clear(): void {
    const operation = {};
    this.operation = operation;
    const errors = this.detach();
    if (this.operation !== operation) return;
    this.publish({ kind: "reset" });
    if (errors.length) this.publish({ kind: "error", error: new AggregateError(errors, "Region release failed.") });
  }

  private detach(): unknown[] {
    const active = this.active;
    this.active = null; // Fence callbacks before unsubscribe/release can reenter.
    const errors: unknown[] = [];
    if (active) {
      active.signal.removeEventListener("abort", active.abort);
      const regions = [...active.regions.values()];
      let regionIndex = 0;
      while (regionIndex < regions.length) {
        const held = regions[regionIndex++];
        try { held.unsubscribe?.(); } catch (error) { errors.push(error); }
        held.unsubscribe = null;
        const sources = [...held.sources];
        let sourceIndex = 0;
        while (sourceIndex < sources.length) held.reader.invalidate(sources[sourceIndex++]);
      }
      errors.push(...this.releaseGrants(active.grants));
    }
    return errors;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.clear();
  }

  private wake(active: ActiveSet, held: HeldRegion, event: RegionWake): void {
    if (this.active !== active) return;
    let sourceIndex = 0;
    while (sourceIndex < event.sources.length) {
      const source = event.sources[sourceIndex++];
      if (this.active !== active) return;
      if (!held.sources.has(source) || held.lost.has(source)) continue;
      if (event.kind === "loss") {
        held.lost.add(source);
        held.pending.delete(source);
        held.reader.invalidate(source);
        this.publish({ kind: "loss", region: held.reference, source });
      } else {
        held.pending.add(source);
      }
    }
    if (!active.registering) this.drain(active, held);
  }

  private drain(active: ActiveSet, held: HeldRegion): void {
    if (this.active !== active || held.draining) return;
    // Drain a snapshot; schedule reentrant notifications in a microtask to avoid lost wakes.


    const sources = [...held.pending];
    held.pending.clear();
    held.draining = true;
    try {
      let sourceIndex = 0;
      while (sourceIndex < sources.length) {
        const source = sources[sourceIndex++];
        if (this.active !== active) return;
        if (held.lost.has(source)) continue;
        const result = held.reader.read(source);
        this.publish({ kind: "source", region: held.reference, source, result });
      }
    } finally {
      held.draining = false;
      if (this.active === active && held.pending.size && !held.scheduled) {
        held.scheduled = true;
        queueMicrotask(() => {
          held.scheduled = false;
          this.drain(active, held);
        });
      }
    }
  }

  private releaseGrants(grants: readonly GrantedRegion[]): unknown[] {
    const errors: unknown[] = [];
    let grantIndex = 0;
    while (grantIndex < grants.length) {
      const grant = grants[grantIndex++];
      try { grant.release(); } catch (error) { errors.push(error); }
    }
    return errors;
  }
}

function sameReference(left: RegionReference, right: RegionReference): boolean {
  return left.id === right.id && left.epoch === right.epoch
    && left.layout_generation === right.layout_generation;
}

function checkReference(value: RegionReference): void {
  if (!Number.isInteger(value.id) || value.id < 0 || value.id > 0xffff_ffff
    || typeof value.epoch !== "bigint" || value.epoch < 0n || value.epoch > 0xffff_ffff_ffff_ffffn
    || typeof value.layout_generation !== "bigint" || value.layout_generation < 0n
    || value.layout_generation > 0xffff_ffff_ffff_ffffn) {
    throw new Error("Invalid physical region reference.");
  }
}
