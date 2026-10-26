import { usePushSource } from "../frontendRuntime/push/usePushSource.ts";
import { createEffect, createMemo, untrack, type Accessor } from "solid-js";
import {
  normalizeResourceBreakdownQuery,
  normalizeResourceMonitorQuery,
  type ResourceBreakdownQuery,
  type ResourceMonitorQuery
} from "../data/resourceMonitor/resourceMonitorApi.ts";
import { resourceBarResidualDetailMetricIds } from "../features/resourceBreakdown/resourceBarSources.ts";
import type { PushValueSourceFamily } from "../frontendRuntime/push/PushValueSourceFamily.ts";
import type { SourceSnapshot } from "../frontendRuntime/source/SourceSnapshot.ts";
import type { ResourceMonitorSnapshot, ResourceProcessDetailsSnapshot } from "../types.ts";

// Each value belongs only to its current binding. Selection changes the details
// binding; it never releases the bars or borrows their value for another query.
export function createResourceBreakdownBindings(options: {
  active: Accessor<boolean>;
  intervalMs: Accessor<number | null>;
  bars: Accessor<ResourceBreakdownQuery["bars"]>;
  /** 展开或选中的段：真实软件按 id 列出进程，系统/驱动保留按所属资源条的指标 id 列出分项。 */
  detailSoftwareIds: Accessor<readonly string[]>;
  detailResidualMetricIds: Accessor<readonly string[]>;
  barsSource: PushValueSourceFamily<ResourceMonitorQuery, ResourceMonitorSnapshot>;
  detailsSource: PushValueSourceFamily<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>;
  onBars: (value: ResourceMonitorSnapshot) => void;
}) {
  const barsKey = createMemo(() => JSON.stringify({
    active: options.active(), intervalMs: options.intervalMs(),
    query: normalizeResourceMonitorQuery({
      scope: "bars", bars: options.bars(), processDetailSoftwareIds: [],
      residualDetailMetricIds: resourceBarResidualDetailMetricIds(options.bars() ?? []),
      sampleMetricIds: null, visibleColumnIds: [], sortColumnId: "impact",
      sortDirection: "desc", tableMode: "software"
    })
  }));
  const detailsKey = createMemo(() => JSON.stringify({
    active: options.active(), intervalMs: options.intervalMs(),
    query: normalizeResourceBreakdownQuery({
      bars: options.bars(),
      processDetailSoftwareIds: options.detailSoftwareIds(),
      residualDetailMetricIds: options.detailResidualMetricIds()
    })
  }));
  const bars = bindCurrent<ResourceMonitorQuery, ResourceMonitorSnapshot>(barsKey, options.barsSource,
    () => true, options.onBars);
  const details = bindCurrent<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>(detailsKey, options.detailsSource,
    query => query.processDetailSoftwareIds.length > 0 || query.residualDetailMetricIds.length > 0);
  return { bars, details };
}

function bindCurrent<TQuery, TValue>(
  key: Accessor<string>,
  source: PushValueSourceFamily<TQuery, TValue>,
  needed: (query: TQuery) => boolean,
  onValue?: (value: TValue) => void
): Accessor<SourceSnapshot<TValue> | null> {
  const binding = createMemo(() => JSON.parse(key()) as {
    active: boolean; intervalMs: number | null; query: TQuery
  });
  const { snapshot } = usePushSource(source, () => ({
    ...binding(), active: binding().active && needed(binding().query)
  }));
  createEffect(() => {
    const value = snapshot().data;
    if (value) untrack(() => onValue?.(value));
  });
  return snapshot;
}
