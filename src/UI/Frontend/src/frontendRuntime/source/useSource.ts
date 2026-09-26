import {
  createEffect,
  createSignal,
  onCleanup,
  type Accessor
} from "solid-js";
import type {
  SourceDemand,
  SourceHandle
} from "./SourceDescriptor.ts";
import type { SourceSnapshot } from "./SourceSnapshot.ts";

export interface SourceView<T> {
  readonly snapshot: Accessor<SourceSnapshot<T>>;
  refresh(): Promise<SourceSnapshot<T>>;
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
    refresh: () => lease.refresh(),
    acceptAuthoritative: (value) => lease.acceptAuthoritative(value)
  };
}
