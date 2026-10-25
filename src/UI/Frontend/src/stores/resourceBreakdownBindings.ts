import { createEffect, createMemo, createSignal, onCleanup, untrack, type Accessor } from "solid-js";
import {
  normalizeResourceBreakdownQuery,
  normalizeResourceMonitorQuery,
  type ResourceBreakdownQuery,
  type ResourceMonitorQuery
} from "../data/resourceMonitor/resourceMonitorApi.ts";
import type { PushValueSourceFamily } from "../frontendRuntime/push/PushValueSourceFamily.ts";
import type { SourceSnapshot } from "../frontendRuntime/source/SourceSnapshot.ts";
import type { ResourceMonitorSnapshot, ResourceProcessDetailsSnapshot } from "../types.ts";

// Each value belongs only to its current binding. Selection changes the details
// binding; it never releases the bars or borrows their value for another query.
export function createResourceBreakdownBindings(options: {
  active: Accessor<boolean>;
  intervalMs: Accessor<number | null>;
  bars: Accessor<ResourceBreakdownQuery["bars"]>;
  detailSoftwareIds: Accessor<readonly string[]>;
  barsSource: PushValueSourceFamily<ResourceMonitorQuery, ResourceMonitorSnapshot>;
  detailsSource: PushValueSourceFamily<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>;
  onBars: (value: ResourceMonitorSnapshot) => void;
}) {
  const barsKey = createMemo(() => JSON.stringify({
    active: options.active(), intervalMs: options.intervalMs(),
    query: normalizeResourceMonitorQuery({
      scope: "bars", bars: options.bars(), processDetailSoftwareIds: [],
      sampleMetricIds: null, visibleColumnIds: [], sortColumnId: "impact",
      sortDirection: "desc", tableMode: "software"
    })
  }));
  const detailsKey = createMemo(() => JSON.stringify({
    active: options.active(), intervalMs: options.intervalMs(),
    query: normalizeResourceBreakdownQuery({
      // 进程明细（scope=details）不接受显式口径：选了来源的资源条不进明细订阅，也不借按指标 id 的明细。
      bars: options.bars()?.filter((bar) => !bar.source) ?? null,
      processDetailSoftwareIds: options.detailSoftwareIds()
    })
  }));
  const bars = bindCurrent<ResourceMonitorQuery, ResourceMonitorSnapshot>(barsKey, options.barsSource,
    () => true, options.onBars);
  const details = bindCurrent<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>(detailsKey, options.detailsSource,
    query => query.processDetailSoftwareIds.length > 0);
  return { bars, details };
}

function bindCurrent<TQuery, TValue>(
  key: Accessor<string>,
  source: PushValueSourceFamily<TQuery, TValue>,
  needed: (query: TQuery) => boolean,
  onValue?: (value: TValue) => void
): Accessor<SourceSnapshot<TValue> | null> {
  const [current, setCurrent] = createSignal<{ key: string; snapshot: SourceSnapshot<TValue> } | null>(null);
  createEffect(() => {
    const bindingKey = key();
    const binding = JSON.parse(bindingKey) as { active: boolean; intervalMs: number | null; query: TQuery };
    if (!binding.active || binding.intervalMs === null || !needed(binding.query)) return;
    let accepting = true;
    const unsubscribe = source.subscribeSnapshot(binding.query, binding.intervalMs, snapshot => {
      if (!accepting) return;
      setCurrent({ key: JSON.stringify(binding.query), snapshot });
      if (snapshot.data) untrack(() => onValue?.(snapshot.data!));
    });
    onCleanup(() => { accepting = false; unsubscribe(); });
  });
  return createMemo(() => current()?.key === JSON.stringify(JSON.parse(key()).query) ? current()!.snapshot : null);
}
