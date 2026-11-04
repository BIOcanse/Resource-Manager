/* Match localized names, IDs, categories, device names, and details case-insensitively. Query changes reset expansion overrides; keyboard navigation uses expanded groups only. */

/** Default expansion uses backend group keys and device kinds, never device display names. */
export const metricPickerDefaultExpandedGroups: readonly string[] = Object.freeze([
  "cpu", "gpu", "gpu.N", "memory", "disk", "network"
]);

/** Catalog device kinds for common resource categories. */
export const metricPickerDeviceGroupClasses: Readonly<Record<string, string>> = Object.freeze({
  cpu: "cpu",
  gpu_adapter: "gpu",
  memory: "memory",
  storage: "disk",
  network_interface: "network"
});

export function metricPickerDeviceGroupClass(kind: string | null | undefined): string {
  return metricPickerDeviceGroupClasses[kind ?? ""] ?? kind ?? "";
}

/** Normalize indexed GPU group keys to gpu.N. */
export function metricPickerGroupClass(group: string | null | undefined): string {
  const key = String(group ?? "").trim().toLocaleLowerCase();
  return /^gpu\.\d+$/.test(key) ? "gpu.N" : key;
}

export function metricPickerGroupExpandedByDefault(group: string | null | undefined): boolean {
  return metricPickerDefaultExpandedGroups.includes(metricPickerGroupClass(group));
}

/** Trim and lowercase using the current locale; empty means no search. */
export function normalizeMetricPickerQuery(query: string): string {
  return query.trim().toLocaleLowerCase();
}

/** Match any nonempty text against the normalized query. */
export function metricPickerTextsMatch(texts: readonly (string | null | undefined)[], filter: string): boolean {
  if (!filter) return true;
  return texts.some((text) => typeof text === "string" && text.toLocaleLowerCase().includes(filter));
}

export interface MetricPickerGroup<M> {
  /** Key device fields by identity and other entries by category label. */
  readonly key: string;
  /** Keep the expansion category separate from the device label. */
  readonly group: string;
  readonly metrics: readonly M[];
}

/** Preserve first-occurrence group order and input item order. */
export function groupMetricPickerOptions<M extends { readonly group: string }>(
  metrics: readonly M[],
  keyOf: (metric: M) => string,
  groupOf: (metric: M) => string = (metric) => metric.group
): MetricPickerGroup<M>[] {
  const groups = new Map<string, M[]>();
  for (const metric of metrics) {
    const key = keyOf(metric);
    const members = groups.get(key);
    if (members) members.push(metric);
    else groups.set(key, [metric]);
  }
  return [...groups].map(([key, members]) => ({ key, group: groupOf(members[0]), metrics: members }));
}

export interface MetricPickerFoldState {
  /** Normalized query; empty means no search. */
  readonly filter: string;
  /** Expansion overrides for the current query. */
  readonly overrides: ReadonlyMap<string, boolean>;
}

/** Prefer explicit overrides, then search expansion, then category defaults. */
export function metricPickerGroupExpanded(group: Pick<MetricPickerGroup<unknown>, "key" | "group">, state: MetricPickerFoldState): boolean {
  const override = state.overrides.get(group.key);
  if (override !== undefined) return override;
  return state.filter ? true : metricPickerGroupExpandedByDefault(group.group);
}

/** Return selectable items in expanded groups in display order. */
export function metricPickerNavigationIds<M extends { readonly id: string; readonly group: string }>(
  groups: readonly MetricPickerGroup<M>[],
  state: MetricPickerFoldState,
  selectable: (metric: M) => boolean
): string[] {
  const ids: string[] = [];
  for (const group of groups) {
    if (!metricPickerGroupExpanded(group, state)) continue;
    for (const metric of group.metrics) if (selectable(metric)) ids.push(metric.id);
  }
  return ids;
}
