import { createEffect, createMemo, createSignal, onCleanup, untrack, type Accessor } from "solid-js";
import type { SourceSnapshot } from "../source/SourceSnapshot.ts";
import type { CurrentValueSource, PushValueSource, PushValueSourceFamily } from "./PushValueSourceFamily.ts";

export interface PushDemand {
  readonly active: boolean;
  readonly intervalMs: number | null | undefined;
}
export interface PushFamilyDemand<TQuery> extends PushDemand {
  readonly query: TQuery;
}
export interface CurrentValueDemand {
  readonly active: boolean;
}
export interface PushSourceView<TValue> {
  readonly snapshot: Accessor<SourceSnapshot<TValue>>;
}

type Source<TQuery, TValue> = PushValueSourceFamily<TQuery, TValue> | PushValueSource<TValue> | CurrentValueSource<TValue>;
type Demand<TQuery> = PushFamilyDemand<TQuery> | PushDemand | CurrentValueDemand;
type Listener<TValue> = (snapshot: SourceSnapshot<TValue>) => void;

function read<TQuery, TValue>(source: Source<TQuery, TValue>, demand: Demand<TQuery>): SourceSnapshot<TValue> {
  return "query" in demand
    ? (source as PushValueSourceFamily<TQuery, TValue>).readSnapshot(demand.query)
    : (source as PushValueSource<TValue>).readSnapshot();
}

/** Read first; only active demand opens a listener. The caller owns the returned disposal. */
export function bindPushSource<TQuery, TValue>(source: PushValueSourceFamily<TQuery, TValue>, demand: PushFamilyDemand<TQuery>, listener: Listener<TValue>): () => void;
export function bindPushSource<TValue>(source: PushValueSource<TValue>, demand: PushDemand, listener: Listener<TValue>): () => void;
export function bindPushSource<TValue>(source: CurrentValueSource<TValue>, demand: CurrentValueDemand, listener: Listener<TValue>): () => void;
export function bindPushSource<TQuery, TValue>(source: Source<TQuery, TValue>, demand: Demand<TQuery>, listener: Listener<TValue>): () => void {
  return bind(source, demand, listener);
}

function bind<TQuery, TValue>(source: Source<TQuery, TValue>, demand: Demand<TQuery>, listener: Listener<TValue>): () => void {
  let accepting = true;
  const publish: Listener<TValue> = snapshot => {
    if (!accepting) return;
    try { untrack(() => listener(snapshot)); } catch {
      // A consumer failure does not change the current value or sibling listeners.
    }
  };
  publish(read(source, demand));
  let unsubscribe: (() => void) | undefined;
  if (demand.active) {
    if ("intervalMs" in demand) {
      if (demand.intervalMs != null) {
        unsubscribe = "query" in demand
          ? (source as PushValueSourceFamily<TQuery, TValue>).subscribeSnapshot(demand.query, demand.intervalMs, publish)
          : (source as PushValueSource<TValue>).subscribeSnapshot(demand.intervalMs, publish);
      }
    } else {
      unsubscribe = (source as CurrentValueSource<TValue>).subscribeSnapshot(publish);
    }
  }
  return () => { accepting = false; unsubscribe?.(); };
}

export function usePushSource<TQuery, TValue>(source: PushValueSourceFamily<TQuery, TValue>, demand: Accessor<PushFamilyDemand<TQuery>>): PushSourceView<TValue>;
export function usePushSource<TValue>(source: PushValueSource<TValue>, demand: Accessor<PushDemand>): PushSourceView<TValue>;
export function usePushSource<TValue>(source: CurrentValueSource<TValue>, demand: Accessor<CurrentValueDemand>): PushSourceView<TValue>;
export function usePushSource<TQuery, TValue>(source: Source<TQuery, TValue>, demand: Accessor<Demand<TQuery>>): PushSourceView<TValue> {
  // Equivalent demand publications must retain the listener, including startup settings saves.
  const binding = createMemo(demand, undefined, { equals: (left, right) => JSON.stringify(left) === JSON.stringify(right) });
  const [publication, notify] = createSignal(untrack(() => read(source, binding())));
  createEffect(() => {
    const stop = bind(source, binding(), next => notify(() => next));
    onCleanup(stop);
  });
  return { snapshot: () => {
    publication();
    return read(source, binding());
  } };
}
