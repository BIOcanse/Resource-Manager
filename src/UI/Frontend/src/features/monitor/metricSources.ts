import type { MetricDefinition, MetricSourceCandidate } from "../../types";
import { metricOfferedInPicker } from "../../presentation/metricPresentation.ts";
import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { userFacingMetricGroup } from "../../presentation/userFacingText.ts";
import { uiText } from "../../text.ts";
import { deviceFieldScopeKind, fieldCardId } from "./fieldCards.ts";

/* Use backend candidates and selection for component addresses. Unbound fields share reading IDs and source ranks; explicit selection reads only that source and preserves unavailability. */

export interface MetricSourceChoice {
  /** Store the component address for automatic selection or field@device for an explicit source. */
  readonly metricId: string;
  readonly automatic: boolean;
  /** Automatic selection points to the backend-selected source, or null. */
  readonly source: MetricDefinition | null;
  /** Backend-declared preferred source. */
  readonly preferred: boolean;
}

/** Localize the group label while preserving the device name. */
export function metricSourceName(source: MetricDefinition): string {
  return `${localizedMetricLabel(source.id, source.label)} · ${userFacingMetricGroup(source.group)}`;
}

/** Automatic labels include the backend-selected source when available. */
export function metricSourceChoiceLabel(choice: MetricSourceChoice): string {
  return choice.automatic
    ? uiText.metricPicker.sourceAutomatic(choice.source ? metricSourceName(choice.source) : null)
    : metricSourceName(choice.source!);
}

/** Offer sources from the complete catalog; omit controls when fewer than two are available. */
export function metricSourceChoices(
  selectedId: string | null,
  catalog: readonly MetricDefinition[]
): readonly MetricSourceChoice[] {
  if (!selectedId) return [];
  const byId = new Map(catalog.map((metric) => [metric.id, metric]));
  const selected = byId.get(selectedId);
  if (!selected) return [];
  const candidateId = (candidate: MetricSourceCandidate) => fieldCardId(candidate.fieldName, candidate.deviceIdentity);

  // Resolve component membership from its address or candidate source.
  const address = selected.scopeKind !== deviceFieldScopeKind
    ? selected
    : catalog.find((metric) => metric.scopeKind !== deviceFieldScopeKind && metricOfferedInPicker(metric)
      && (metric.candidateSources ?? []).some((candidate) => candidateId(candidate) === selected.id));
  if (address) {
    const candidates = (address.candidateSources ?? [])
      .filter((candidate) => candidate.sourceAvailability !== "unsupported" && byId.has(candidateId(candidate)));
    const chosen = (address.candidateSources ?? []).find((candidate) => candidate.isSelected);
    const choices: MetricSourceChoice[] = [
      { metricId: address.id, automatic: true, source: chosen ? byId.get(candidateId(chosen)) ?? null : null, preferred: false },
      ...candidates.map((candidate) => ({ metricId: candidateId(candidate), automatic: false,
        source: byId.get(candidateId(candidate))!, preferred: candidate.reading?.isPreferredSource ?? false }))
    ];
    return choices.length > 1 ? choices : [];
  }

  const readingId = selected.reading?.id;
  if (!readingId) return [];
  const sources = catalog
    .filter((metric) => metric.scopeKind === deviceFieldScopeKind
      && metric.reading?.id === readingId && metric.reading.sourceAvailability !== "unsupported")
    // Prefer the backend-preferred source when ranks tie.
    .sort((left, right) => left.reading!.sourceRank - right.reading!.sourceRank
      || Number(right.reading!.isPreferredSource) - Number(left.reading!.isPreferredSource));
  return sources.length > 1
    ? sources.map((source) => ({ metricId: source.id, automatic: false, source, preferred: source.reading!.isPreferredSource }))
    : [];
}
