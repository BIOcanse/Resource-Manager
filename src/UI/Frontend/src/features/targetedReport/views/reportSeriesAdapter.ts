import type { RecordingReport } from "../targetedRecordingApi.ts";
import { seriesSet, type ReportSeries, type SeriesSet } from "./seriesModel.ts";

/**
 * 今天的报告（targeted.report.v1）→ 序列模型。新线格式到来时换掉这一个文件。
 *
 * - 帧间隔：帧时间（ms）和帧率（1000 / 帧时间，FPS）各出两条，一条按时间（帧结束时刻），一条按帧序号
 *   （报告里的顺序）。帧时间不大于 0 的帧在帧率序列里是缺口。
 * - 资源采样：软件段和整机段的每个指标各一条时间序列；某次采样没有这个指标或值为 null 时是缺口。
 * - 报告头带录制项（recording.items 非空，按录制配置录的报告）时，资源序列按录制项逐项出，顺序同录制项：
 *   software 项读采样的软件段，role 为 software:<id>；metric 项读整机段，role 为 system:<id>；单位用录制项的单位。
 *   各项间隔可以不同，某次采样里没有这一项的键表示这次没采它，不出点；键在但值为 null 是缺口。
 *   帧序列照旧。旧报告（没有录制项）走下面按采样里出现的指标出序列的路径。
 * - 时间以录制开始为 0（毫秒）。开始时间解析不出来时以 0 为原点，横轴直接显示原始毫秒数，不另找原点。
 *   时刻解析不出来的帧或采样不进序列。
 */
export interface SeriesLabels {
  frameTime: string;
  frameRate: string;
  software: string;
  system: string;
  metric: (metricId: string) => string;
  /** 录制项的名称（按录制项出序列时用；和报告的录制项表同名）。 */
  item: (kind: "software" | "metric", id: string) => string;
}

export const frameRoles = { frameTime: "frames", frameRate: "frameRate" } as const;

/** 资源指标的原始单位：后端发原始数，单位按指标 ID 的类别决定。 */
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

/**
 * 一个序列 id 或 role 的名称，这份报告里没有这条序列时也能给出（视图上写“这份报告没有这条序列”时用）：
 * frames.* / frameRate.* 和它们的 role，software:<指标>、system:<指标>。认不出的返回 null，调用方显示原样的 id。
 */
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

/** 去掉 x 不是有限数的点，按 x 升序排好（已经有序时不排）。返回新数组。 */
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
