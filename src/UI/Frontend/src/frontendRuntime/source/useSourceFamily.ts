import { createComputed, createSignal, onCleanup, untrack, type Accessor } from "solid-js";
import type { SourceDemand, SourceFamily } from "./SourceDescriptor.ts";
import { SourceLeaseBinding } from "./SourceLeaseBinding.ts";
import { canonicalizeSourceQuery } from "./SourceRegistry.ts";
import type { SourceView } from "./useSource.ts";

/** Binds the selected query before render; the registry remains the value owner. */
export function useSourceFamily<TQuery, TValue>(
  family: SourceFamily<TQuery, TValue>,
  query: Accessor<TQuery>,
  demand: Accessor<SourceDemand>
): SourceView<TValue> {
  const initialQuery = untrack(query);
  let lease = untrack(() => family.acquire(initialQuery, { active: false }));
  const [snapshot, setSnapshot] = createSignal(untrack(() => lease.snapshot));
  const binding = new SourceLeaseBinding<TValue>((next) => setSnapshot(() => next));
  untrack(() => binding.switch(canonicalizeSourceQuery(initialQuery).key, () => lease));
  onCleanup(() => binding.release());

  createComputed(() => {
    const nextQuery = query();
    const nextDemand = demand();
    // Only query and demand are dependencies, never registry/listener internals.
    untrack(() => {
      lease = binding.switch(canonicalizeSourceQuery(nextQuery).key,
        () => family.acquire(nextQuery, { active: false }));
      lease.setDemand(nextDemand);
    });
  });

  return {
    snapshot,
    refresh: (options) => lease.refresh(options),
    invalidate: () => lease.invalidate(),
    acceptAuthoritative: (value) => lease.acceptAuthoritative(value)
  };
}
