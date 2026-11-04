import { formatTrimmedDigits } from "../../presentation/interfaceNumbers.ts";
import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import type { ResourceBarQuery } from "../../data/resourceMonitor/resourceMonitorApi.ts";
import type { ResourceBarSettings, ResourceBreakdownBar } from "../../types.ts";
import { localizedMetricLabel, resourceMetricBasis } from "../../presentation/metricLabels.ts";
import {
  automaticProcessSourceLabel,
  processMemoryQuantity,
  processSourceName,
  processResourceSources
} from "../../presentation/processMemorySources.ts";
import { normalizeResourceBarScaleMode } from "./resourceBarScaleCapabilities.ts";
import { uiText } from "../../text.ts";

/* Automatic bars subscribe by metric ID; explicit sources subscribe by field measure. Keep backend totals and unavailable states without substituting sources; details use the same measure. */

/** Associate backend data with the configured item identity for ordering and selection. */
export interface ResourceBarView extends ResourceBreakdownBar {
  /** Store null for automatic selection. */
  readonly source: string | null;
  /** Share source names and explanations between bar headings and details. */
  readonly label: string;
  readonly basis: string | undefined;
  /** Use static source explanations without borrowing the bars sampling window for details. */
  readonly sourceBasis: string | undefined;
}

/** Offer automatic selection plus supported fields compared against this bar's metric; omit empty choices. */
export function resourceBarSourceChoices(metricId: string, fields: readonly ProcessMetricField[]): readonly (string | null)[] {
  const sources = processResourceSources([metricId], fields);
  return sources.length === 0 ? [] : [null, ...sources.map((field) => field.name)];
}

/** Always request the saved explicit field, even before the catalog arrives. */
export function resourceBarSource(bar: Pick<ResourceBarSettings, "metricId" | "source">): string | null {
  return bar.source || null;
}

/** Request physical-memory residual counters in bars without triggering page-frame reads. Software rows remain complete; detail selectors control only their child lists. */
export function resourceBarResidualDetailMetricIds(bars: readonly Pick<ResourceBarQuery, "metricId">[]): string[] {
  return bars.some((bar) => bar.metricId === "memory.usage") ? ["memory.usage"] : [];
}

/** Describe the metric, scale, and optional explicit measure used by the subscription. */
export function resourceBarQuery(bar: ResourceBarSettings): ResourceBarQuery {
  return { metricId: bar.metricId, scaleMode: normalizeResourceBarScaleMode(bar.metricId, bar.scaleMode), source: resourceBarSource(bar) };
}

/** Preserve configured order and omit bars absent from backend output. */
export function resourceBarViews(
  snapshotBars: readonly ResourceBreakdownBar[],
  settings: readonly ResourceBarSettings[],
  fields: readonly ProcessMetricField[]
): ResourceBarView[] {
  const byId = new Map(snapshotBars.map((bar) => [bar.metricId, bar]));
  const views: ResourceBarView[] = [];
  for (const setting of settings) {
    const source = resourceBarSource(setting);
    const bar = byId.get(source ?? setting.metricId);
    if (!bar) continue;
    const text = resourceBarText({ metricId: setting.metricId, source, processFieldName: bar.processFieldName }, fields, bar.additiveAcrossProcesses);
    const window = bar.windowMilliseconds === null ? null
      : uiText.resourceTable.factsWindow(formatTrimmedDigits(bar.windowMilliseconds / 1000, 3));
    views.push({ ...bar, metricId: setting.metricId, source, label: text.label, sourceBasis: text.basis,
      basis: [text.basis, window].filter(Boolean).join("\n") || undefined });
  }
  return views;
}

export interface ResourceBarText {
  readonly label: string;
  readonly basis: string | undefined;
}

/** Use frame additivity for visible bars and catalog additivity for editor choices; null means undeclared. */
export function resourceBarText(bar: Pick<ResourceBarView, "metricId" | "source" | "processFieldName">, fields: readonly ProcessMetricField[],
  additive: boolean | null): ResourceBarText {
  if (!bar.source) {
    const quantity = bar.processFieldName ? processMemoryQuantity(bar.processFieldName, fields) : null;
    const basis = withAdditivity(additive, quantity === "working_set_including_shared"
      ? uiText.resourceTable.memorySourceBasis.working_set_including_shared : resourceMetricBasis(bar.metricId));
    return { label: localizedMetricLabel(bar.metricId),
      basis: bar.processFieldName ? [automaticProcessSourceLabel(bar.processFieldName, fields), basis].filter(Boolean).join("\n") : basis };
  }
  const quantity = processMemoryQuantity(bar.source, fields);
  return {
    label: quantity ? uiText.resourceTable.memoryColumnBySource(processSourceName(bar.source, fields))
      : `${localizedMetricLabel(bar.metricId)} · ${processSourceName(bar.source, fields)}`,
    basis: withAdditivity(additive, quantity === "working_set_including_shared" ? uiText.resourceTable.memorySourceBasis.working_set_including_shared
      : quantity === "private_working_set" ? uiText.resourceBreakdown.memoryBasis
        : quantity === "private_commit" ? uiText.resourceBreakdown.commitBasis : undefined)
  };
}

/** Keep the measure explanation when prefixing nonadditivity. */
function withAdditivity(additive: boolean | null, basis: string | undefined): string | undefined {
  return additive === false ? [uiText.resourceTable.nonAdditiveBasis, basis].filter(Boolean).join("\n") : basis;
}

/** Automatic option labels include the currently read field when available. */
export function resourceBarSourceLabel(source: string | null, fields: readonly ProcessMetricField[], automaticField: string | null): string {
  return source ? processSourceName(source, fields) : automaticProcessSourceLabel(automaticField, fields);
}

/** Use catalog additivity for explicit editor sources; automatic or unknown fields return null. */
export function processSourceAdditivity(source: string | null, fields: readonly ProcessMetricField[]): boolean | null {
  return source === null ? null : fields.find((field) => field.name === source)?.additiveAcrossProcesses ?? null;
}
