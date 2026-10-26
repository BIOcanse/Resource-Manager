import {
  createEffect,
  createSignal,
  onCleanup,
  type Accessor
} from "solid-js";
import type {
  SourceDemand,
  SourceRefreshOptions,
  SourceHandle
} from "./SourceDescriptor.ts";
import type { SourceSnapshot } from "./SourceSnapshot.ts";

export interface SourceView<T> {
  readonly snapshot: Accessor<SourceSnapshot<T>>;
  refresh(options?: SourceRefreshOptions): Promise<SourceSnapshot<T>>;
  invalidate(): Promise<SourceSnapshot<T>>;
  acceptAuthoritative(value: T): SourceSnapshot<T>;
}

export function useSource<T>(
  handle: SourceHandle<T>,
  demand: Accessor<SourceDemand>
): SourceView<T> {
  const lease = handle.acquire({ active: false });
  const [snapshot, setSnapshot] = createSignal(lease.snapshot);
  const unsubscribe = lease.subscribe((next) => setSnapshot(() => next));

  createEffect(() => lease.setDemand(demand()));
  onCleanup(() => {
    unsubscribe();
    lease.release();
  });

  return {
    snapshot,
    refresh: options => lease.refresh(options),
    invalidate: () => lease.invalidate(),
    acceptAuthoritative: (value) => lease.acceptAuthoritative(value)
  };
}
