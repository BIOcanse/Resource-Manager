import type { RecordingReport } from "../targetedRecordingApi.ts";
import { seriesSet, type ReportSeries, type SeriesSet } from "./seriesModel.ts";

/** Adapt targeted.report.v1 to series. Preserve item order and distinguish unsampled keys from null gaps. Times are relative to recording start, or epoch zero if invalid; skip invalid timestamps. */
export interface SeriesLabels {
  frameTime: string;
  frameRate: string;
  software: string;
  system: string;
  metric: (metricId: string) => string;
  /** Share recording-item labels with the metadata table. */
  item: (kind: "software" | "metric", id: string) => string;
}

export const frameRoles = { frameTime: "frames", frameRate: "frameRate" } as const;

/** Infer raw units from the metric category. */
export function metricUnit(id: string) {
  if (/^(disk\.|network\.)/.test(id)) return "B/s";
  if (/^(memory\.|virtualMemory\.)/.test(id) || /\.vram$/.test(id)) return "B";
  if (/temperature$/.test(id)) return "°C";
  if (/frequency$/.test(id)) return "MHz";
  return "%";
}

export function seriesFromReport(report: RecordingReport, labels: SeriesLabels): SeriesSet {
  const parsedOrigin = Date.parse(report.recording.startedAt);
  const origin = Number.isFinite(parsedOrigin) ? parsedOrigin : 0;
  const series: ReportSeries[] = [];

  const frames = report.frameIntervals;
  if (frames.length > 0) {
    const times = new Float64Array(frames.length);
    const durations = new Float64Array(frames.length);
    for (let index = 0; index < frames.length; index++) {
      times[index] = Date.parse(frames[index].endedAt) - origin;
      durations[index] = frames[index].durationMs;
    }
    const rates = new Float64Array(frames.length);
    for (let index = 0; index < frames.length; index++) {
      rates[index] = durations[index] > 0 ? 1000 / durations[index] : Number.NaN;
    }
    const indexes = new Float64Array(frames.length);
    for (let index = 0; index < frames.length; index++) indexes[index] = index;
    const timed = (values: Float64Array) => sortedPairs(times, values);
    const frameTime = timed(durations);
    const frameRate = timed(rates);
    series.push(
      { id: "frames.time", role: frameRoles.frameTime, label: labels.frameTime, unit: "ms", kind: "time",
        x: frameTime.x, y: frameTime.y, meta: { scope: "frames", metricId: null } },
      { id: "frames.index", role: frameRoles.frameTime, label: labels.frameTime, unit: "ms", kind: "frame",
        x: indexes, y: durations, meta: { scope: "frames", metricId: null } },
      { id: "frameRate.time", role: frameRoles.frameRate, label: labels.frameRate, unit: "FPS", kind: "time",
        x: frameRate.x, y: frameRate.y, meta: { scope: "frames", metricId: null } },
      { id: "frameRate.index", role: frameRoles.frameRate, label: labels.frameRate, unit: "FPS", kind: "frame",
        x: indexes, y: rates, meta: { scope: "frames", metricId: null } });
  }

  const samples = report.resourceSamples;
  const sampleTimes = new Float64Array(samples.length);
  for (let index = 0; index < samples.length; index++) sampleTimes[index] = Date.parse(samples[index].capturedAt) - origin;
  const items = report.recording.items ?? [];
  if (items.length > 0) {
    for (const item of items) {
      if (item.kind === "frames") continue;
      const scope = item.kind === "software" ? "software" : "system";
      const times: number[] = [];
      const values: number[] = [];
      for (let index = 0; index < samples.length; index++) {
        const segment = samples[index][scope];
        if (!Object.hasOwn(segment, item.id)) continue;
        times.push(sampleTimes[index]);
        values.push(segment[item.id] ?? Number.NaN);
      }
      const pairs = sortedPairs(Float64Array.from(times), Float64Array.from(values));
      const role = `${scope}:${item.id}`;
      series.push({ id: role, role, label: `${scope === "software" ? labels.software : labels.system} · ${labels.item(item.kind, item.id)}`,
        unit: item.unit, kind: "time", x: pairs.x, y: pairs.y, meta: { scope, metricId: item.id } });
    }
    return seriesSet(series);
  }
  for (const scope of ["software", "system"] as const) {
    const metrics = new Set<string>();
    for (const sample of samples) for (const metric of Object.keys(sample[scope])) metrics.add(metric);
    for (const metric of [...metrics].sort()) {
      const values = new Float64Array(samples.length);
      for (let index = 0; index < samples.length; index++) values[index] = samples[index][scope][metric] ?? Number.NaN;
      const pairs = sortedPairs(sampleTimes, values);
      const role = `${scope}:${metric}`;
      series.push({ id: role, role, label: `${scope === "software" ? labels.software : labels.system} · ${labels.metric(metric)}`,
        unit: metricUnit(metric), kind: "time", x: pairs.x, y: pairs.y, meta: { scope, metricId: metric } });
    }
  }
  return seriesSet(series);
}

/** Resolve known series IDs or roles even when absent; return null for unknown references. */
export function seriesLabelForId(id: string, labels: SeriesLabels): string | null {
  if (id === frameRoles.frameTime || id.startsWith(`${frameRoles.frameTime}.`)) return labels.frameTime;
  if (id === frameRoles.frameRate || id.startsWith(`${frameRoles.frameRate}.`)) return labels.frameRate;
  const separator = id.indexOf(":");
  const scope = id.slice(0, separator);
  if (separator > 0 && separator < id.length - 1 && (scope === "software" || scope === "system")) {
    return `${scope === "software" ? labels.software : labels.system} · ${labels.metric(id.slice(separator + 1))}`;
  }
  return null;
}

/** Return a new array with finite X values in ascending order; skip sorting when already ordered. */
export function sortedPairs(x: Float64Array, y: Float64Array): { x: Float64Array; y: Float64Array } {
  let count = 0;
  let ordered = true;
  let previous = Number.NEGATIVE_INFINITY;
  for (let index = 0; index < x.length; index++) {
    if (!Number.isFinite(x[index])) continue;
    if (x[index] < previous) ordered = false;
    previous = x[index];
    count++;
  }
  const order = new Uint32Array(count);
  let next = 0;
  for (let index = 0; index < x.length; index++) if (Number.isFinite(x[index])) order[next++] = index;
  if (!ordered) order.sort((left, right) => x[left] - x[right] || left - right);
  const outX = new Float64Array(count);
  const outY = new Float64Array(count);
  for (let index = 0; index < count; index++) {
    outX[index] = x[order[index]];
    outY[index] = y[order[index]];
  }
  return { x: outX, y: outY };
}
