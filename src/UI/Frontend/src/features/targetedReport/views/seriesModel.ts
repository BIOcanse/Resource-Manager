/** Views consume series independently of report wire format. IDs are report-local; roles are stable across reports. X is ascending time or frame index; Y has equal length and uses NaN for gaps. */
export type SeriesDomain = "time" | "frame";

export interface ReportSeries {
  id: string;
  role: string;
  label: string;
  /** Preserve raw measurement units. */
  unit: string;
  kind: SeriesDomain;
  x: Float64Array;
  y: Float64Array;
  meta: { scope: "frames" | "software" | "system"; metricId: string | null };
}

export interface SeriesSet {
  series: readonly ReportSeries[];
  byId: ReadonlyMap<string, ReportSeries>;
}

export function seriesSet(series: readonly ReportSeries[]): SeriesSet {
  return { series, byId: new Map(series.map((item) => [item.id, item])) };
}

/** Prefer the requested X domain for this role, then its first series; return null if absent. */
export function seriesIdForRole(set: SeriesSet, role: string, kind: SeriesDomain): string | null {
  let first: string | null = null;
  for (const item of set.series) {
    if (item.role !== role) continue;
    if (item.kind === kind) return item.id;
    first ??= item.id;
  }
  return first;
}
