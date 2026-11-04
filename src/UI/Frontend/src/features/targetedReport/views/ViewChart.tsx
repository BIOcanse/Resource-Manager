import { createEffect, createMemo, For, onCleanup, onMount, Show } from "solid-js";
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds.ts";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { useWhileNeeded } from "../../../frontendWork/useWhileNeeded.ts";
import { observeDocumentAppearance } from "../../../presentation/documentAppearance.ts";
import { uiText } from "../../../text.ts";
import { seriesLabelForId } from "./reportSeriesAdapter.ts";
import { localizedSeriesLabels } from "./seriesLabels.ts";
import type { SeriesSet } from "./seriesModel.ts";
import { buildViewPlot, type PlotModel, type PlotSeries } from "./viewCompute.ts";
import { formatElapsed, tickText, unitScale, type UnitScale } from "./viewFormat.ts";
import { xSeriesId, type ReportView, type ViewAxisSide, type ViewIssue, type ViewRange } from "./viewModel.ts";

/** Redraw only while needed; retain the chart while hidden and destroy it on unmount. Drag selection writes range.x; automatic axes use data bounds with a zero-inclusive lower Y bound. */
const chartHeight = 240;

/** Cycle through theme colors for Y series. */
const seriesColorVariables = ["--accent", "--info", "--warn", "--ok", "--danger", "--resource-kind-other"] as const;

export function ViewChart(props: {
  view: ReportView;
  set: SeriesSet;
  onSelectRange: (range: ViewRange) => void;
  /** Expose the computed histogram bin count to the editor. */
  onPlot?: (plot: PlotModel) => void;
}) {
  const frontendWork = useFrontendWork();
  const copy = () => uiText.targetedReport.views;
  const plot = createMemo(() => buildViewPlot(props.view, props.set));
  createEffect(() => props.onPlot?.(plot()));

  let host: HTMLDivElement | undefined;
  let chart: uPlot | null = null;
  let frame = 0;
  let dirty = true;

  const needed = () => frontendWork.isNeeded(frontendWorkIds.targetedReportsPage);
  const destroy = () => {
    chart?.destroy();
    chart = null;
  };
  const rebuild = () => {
    dirty = false;
    destroy();
    const current = plot();
    if (!host || current.status !== "ready") return;
    chart = new uPlot(chartOptions(current, props.view, props.set, host, (range) => props.onSelectRange(range)),
      current.data as uPlot.AlignedData, host);
  };
  const rebuildSoon = () => {
    dirty = true;
    if (frame || !needed()) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      rebuild();
    });
  };

  // Schedule one frame while needed; otherwise mark the chart dirty.
  createEffect(() => {
    plot();
    rebuildSoon();
  });
  onMount(() => rebuildSoon());
  onCleanup(() => {
    if (frame) cancelAnimationFrame(frame);
    destroy();
  });

  useWhileNeeded(frontendWorkIds.targetedReportsPage, () => {
    if (dirty) rebuildSoon();
    const observer = new ResizeObserver(() => {
      if (chart && host) chart.setSize({ width: Math.max(1, host.clientWidth), height: chartHeight });
    });
    if (host) observer.observe(host);
    const stopAppearance = observeDocumentAppearance(rebuildSoon);
    return () => {
      observer.disconnect();
      stopAppearance();
      if (frame) {
        cancelAnimationFrame(frame);
        frame = 0;
      }
    };
  });

  return (
    <div class="targeted-report-view-chart">
      <Show when={plot().status === "invalid"}>
        <ul class="targeted-report-note warning targeted-report-view-issues" role="status">
          <For each={plot().issues}>{(issue) => <li>{issueText(issue, props.set)}</li>}</For>
        </ul>
      </Show>
      <Show when={plot().status === "empty"}>
        <p class="targeted-report-note" role="status">{copy().noData}</p>
      </Show>
      <div ref={host} class="targeted-report-view-plot" role="img" aria-label={props.view.name}
        hidden={plot().status !== "ready"} />
    </div>
  );
}

export function issueText(issue: ViewIssue, set: SeriesSet) {
  const text = uiText.targetedReport.views.issues;
  const label = (id: string) => set.byId.get(id)?.label ?? seriesLabelForId(id, localizedSeriesLabels()) ?? id;
  switch (issue.code) {
    case "name-empty": return text.nameEmpty;
    case "no-series": return text.noSeries;
    case "too-many-series": return text.tooManySeries;
    case "series-missing": return text.seriesMissing(label(issue.seriesId));
    case "series-domain": return text.seriesDomain(label(issue.seriesId));
    case "x-series-missing": return text.xSeriesMissing(label(issue.seriesId));
    case "x-series-domain": return text.xSeriesDomain(label(issue.seriesId));
    case "unit-mixed": return text.unitMixed;
    case "range-order": return text.rangeOrder(uiText.targetedReport.views.rangeNames[issue.range]);
    case "value-out-of-limits": return text.outOfLimits(uiText.targetedReport.views.limitNames[issue.field]);
  }
}

/** Resolve theme colors for canvas and apply alpha as rgba. */
function themeColor(element: Element, variable: string, alpha = 1) {
  const raw = getComputedStyle(element).getPropertyValue(variable).trim();
  const context = document.createElement("canvas").getContext("2d");
  if (!context || !raw) return raw;
  context.fillStyle = raw;
  const normalized = String(context.fillStyle);
  if (alpha >= 1) return normalized;
  const hex = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(normalized);
  if (hex) return `rgba(${parseInt(hex[1], 16)}, ${parseInt(hex[2], 16)}, ${parseInt(hex[3], 16)}, ${alpha})`;
  const rgba = /^rgba?\(([^,]+),([^,]+),([^,)]+)/.exec(normalized);
  return rgba ? `rgba(${rgba[1].trim()}, ${rgba[2].trim()}, ${rgba[3].trim()}, ${alpha})` : normalized;
}

function largestOnAxis(plot: PlotModel, side: ViewAxisSide) {
  let largest = 0;
  plot.series.forEach((series, index) => {
    if (series.axis !== side) return;
    for (const value of plot.data[index + 1]) if (typeof value === "number" && Math.abs(value) > largest) largest = Math.abs(value);
  });
  return largest;
}

const countIncrements = Array.from({ length: 10 }, (_, power) => [1, 2, 5].map((step) => step * 10 ** power)).flat();

function axisScale(unit: string, largest: number): UnitScale {
  const copy = uiText.targetedReport.views;
  if (unit === "count") return { unit: copy.units.count, format: (value) => String(Math.round(value)) };
  return unitScale(unit, largest);
}

function chartOptions(plot: PlotModel, view: ReportView, set: SeriesSet, host: HTMLElement,
  onSelectRange: (range: ViewRange) => void): uPlot.Options {
  const copy = uiText.targetedReport.views;
  const text = themeColor(host, "--muted");
  const grid = themeColor(host, "--line");
  const scales: Record<ViewAxisSide, UnitScale | null> = {
    left: plot.axes.left ? axisScale(plot.axes.left.unit, largestOnAxis(plot, "left")) : null,
    right: plot.axes.right ? axisScale(plot.axes.right.unit, largestOnAxis(plot, "right")) : null
  };
  const xLargest = plot.xExtent ? Math.max(Math.abs(plot.xExtent[0]), Math.abs(plot.xExtent[1])) : 0;
  const xScale = plot.x.mode === "value" ? unitScale(plot.x.unit, xLargest) : null;
  const xSeries = xSeriesId(view.x);
  const xTitle = plot.x.mode === "time" ? copy.axisTitles.time
    : plot.x.mode === "frames" ? copy.axisTitles.frames
    : plot.x.mode === "percent" ? copy.axisTitles.percentile
    : plot.x.mode === "category" ? ""
    : [xSeries !== null ? set.byId.get(xSeries)?.label : null, xScale?.unit].filter(Boolean).join(" · ");
  const xTick = (value: number) => plot.x.mode === "time" ? formatElapsed(value).replace(/ s$/, "")
    : plot.x.mode === "percent" ? String(Number(value.toFixed(2)))
    : xScale ? tickText(xScale, value) : String(Number(value.toFixed(2)));
  const categoryLabel = (index: number) => {
    const category = plot.x.categories[index];
    return category ? copy.stats[category.stat] : "";
  };

  const range = (configured: ViewRange, includeZero: boolean): uPlot.Scale.Range =>
    (_self, dataMin, dataMax) => {
      const low = dataMin ?? 0;
      const high = dataMax ?? 1;
      const auto = includeZero ? uPlot.rangeNum(Math.min(0, low), high, 0.1, true) : [low, high] as uPlot.Range.MinMax;
      const min = configured.min ?? auto[0] ?? 0;
      const max = configured.max ?? auto[1] ?? 1;
      return max > min ? [min, max] : [min, min + 1];
    };
  const xRange: uPlot.Scale.Range = plot.x.mode === "category"
    ? () => [plot.xExtent![0], plot.xExtent![1]]
    : (_self, dataMin, dataMax) => {
      const min = view.range.x.min ?? plot.xExtent?.[0] ?? dataMin ?? 0;
      const max = view.range.x.max ?? plot.xExtent?.[1] ?? dataMax ?? 1;
      return max > min ? [min, max] : [min - 0.5, min + 0.5];
    };

  const series: uPlot.Series[] = [{
    label: xTitle || copy.fields.x,
    value: (_self, value) => value === null || value === undefined ? "–"
      : plot.x.mode === "category" ? categoryLabel(value) : plot.x.mode === "time" ? formatElapsed(value) : xTick(value)
  }];
  const stepped = view.aggregation.method !== "none";
  for (const item of plot.series) series.push(plotSeries(item, plot, host, scales[item.axis], stepped));

  const axes: uPlot.Axis[] = [{
    scale: "x", stroke: text, grid: { stroke: grid, width: 1 }, ticks: { stroke: grid, width: 1 },
    label: xTitle, labelSize: xTitle ? 20 : 0,
    ...(plot.x.mode === "category" ? {
      splits: () => categoryCenters(plot),
      values: (_self: uPlot, splits: number[]) => splits.map((value) => categoryLabel(Math.floor(value)))
    } : { values: (_self: uPlot, splits: number[]) => splits.map(xTick) })
  }];
  const sideAxis = (side: ViewAxisSide): uPlot.Axis => {
    const scale = scales[side]!;
    return { scale: side, side: side === "left" ? 3 : 1, stroke: text, label: scale.unit, labelSize: 18, size: 56,
      grid: { show: side === "left" || !scales.left, stroke: grid, width: 1 }, ticks: { stroke: grid, width: 1 },
      // Use integer count ticks to avoid repeated labels after rounding.
      ...(plot.axes[side]?.unit === "count" ? { incrs: countIncrements } : {}),
      values: (_self: uPlot, splits: number[]) => splits.map((value) => tickText(scale, value)) };
  };
  if (scales.left) axes.push(sideAxis("left"));
  if (scales.right) axes.push(sideAxis("right"));

  const selectable = plot.x.mode !== "category";
  return {
    width: Math.max(1, host.clientWidth),
    height: chartHeight,
    scales: {
      x: { time: false, range: xRange },
      left: { range: range(view.range.left, true) },
      right: { range: range(view.range.right, true) }
    },
    series,
    axes,
    legend: { show: true, live: true },
    cursor: { drag: { x: selectable, y: false, setScale: false } },
    select: { show: selectable, left: 0, top: 0, width: 0, height: 0 },
    hooks: {
      setSelect: [(self) => {
        const { left, width } = self.select;
        if (!selectable || width < 2) return;
        const min = self.posToVal(left, "x");
        const max = self.posToVal(left + width, "x");
        self.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false);
        if (Number.isFinite(min) && Number.isFinite(max) && max > min) onSelectRange({ min, max });
      }]
    }
  };
}

/** Hold aggregated time and frame values from each window start to the next. */
function plotSeries(item: PlotSeries, plot: PlotModel, host: HTMLElement, scale: UnitScale | null,
  stepped: boolean): uPlot.Series {
  const copy = uiText.targetedReport.views;
  const variable = seriesColorVariables[item.sourceIndex % seriesColorVariables.length];
  const stroke = themeColor(host, variable);
  const label = item.role === "cdf" ? `${item.label} · ${copy.cdfOverlay}`
    : item.role === "count" ? `${item.label} · ${copy.units.count}` : item.label;
  const value = (_self: uPlot, raw: number | null) => raw === null || raw === undefined ? "–"
    : scale ? scale.format(raw) : String(raw);
  const base: uPlot.Series = { label, scale: item.axis, stroke, width: 1.5, value, spanGaps: false };
  if (item.style === "bars") {
    const histogramBars = plot.x.mode !== "category";
    return { ...base, fill: themeColor(host, variable, 0.35), points: { show: false },
      paths: uPlot.paths.bars!({ align: histogramBars ? 1 : 0, size: histogramBars ? [1, Infinity] : [0.8, 64], gap: 1 }) };
  }
  if (item.style === "points") {
    return { ...base, width: 0, paths: () => null, points: { show: true, size: 4, stroke, fill: stroke } };
  }
  const steps = stepped && item.role === "value" && (plot.x.mode === "time" || plot.x.mode === "frames")
    ? { paths: uPlot.paths.stepped!({ align: 1 }) } : {};
  if (item.style === "area") return { ...base, ...steps, fill: themeColor(host, variable, 0.2), points: { show: false } };
  if (item.role === "cdf") return { ...base, dash: [6, 4], points: { show: false } };
  return { ...base, ...steps, points: { show: false } };
}

/** Center each summary tick on its statistic group. */
function categoryCenters(plot: PlotModel) {
  const centers: number[] = [];
  let start = 0;
  const categories = plot.x.categories;
  for (let index = 1; index <= categories.length; index++) {
    if (index === categories.length || categories[index].stat !== categories[start].stat) {
      centers.push((start + index - 1) / 2);
      start = index;
    }
  }
  return centers;
}
