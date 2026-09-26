import { createEffect, createMemo, For, onCleanup, onMount, Show } from "solid-js";
import type { MetricDefinition, MetricSnapshot } from "../types";
import type { ObservationState } from "../observation/observationState";
import {
  appendPerformancePoint,
  buildPerformanceRuns,
  type PerformancePoint,
  type PerformanceSeries
} from "./resourcePerformanceHistory.ts";
import { uiText } from "../text.ts";

const palette = ["#0f8f7b", "#4f7fd8", "#d79429", "#8762c9", "#c64e64", "#5894a0", "#65922f", "#9a7130"];

interface PerformanceCanvasTheme {
  panel: string;
  card: string;
  border: string;
  text: string;
  muted: string;
  grid: string;
}

export function ResourcePerformancePanel(props: {
  catalog: MetricDefinition[];
  snapshot: MetricSnapshot | null;
  observation: ObservationState;
}) {
  let canvas: HTMLCanvasElement | undefined;
  let frame = 0;
  const series = createMemo(() => performanceSeries(props.catalog));
  const latest = createMemo(() => {
    const snapshot = props.snapshot;
    if (!snapshot) {
      return null;
    }
    return appendPerformancePoint(snapshot, series());
  });
  const currentFrame = createMemo<PerformancePoint[]>(() => {
    const point = latest();
    return point ? [point] : [];
  });

  const drawSoon = () => {
    if (frame) {
      return;
    }

    frame = requestAnimationFrame(() => {
      frame = 0;
      drawPerformanceCanvas(canvas, series(), currentFrame());
    });
  };

  createEffect(() => {
    currentFrame();
    series();
    drawSoon();
  });

  onMount(() => {
    const observer = new ResizeObserver(drawSoon);
    const themeObserver = new MutationObserver(drawSoon);
    if (canvas) {
      observer.observe(canvas);
    }
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["data-theme"]
    });
    drawSoon();
    onCleanup(() => {
      observer.disconnect();
      themeObserver.disconnect();
      if (frame) {
        cancelAnimationFrame(frame);
      }
    });
  });

  return (
    <div class="resource-performance-panel" data-renderer="canvas2d">
      <canvas ref={canvas} aria-hidden="true" />
      <div
        id="resourcePerformanceSummary"
        class="resource-performance-summary"
        role="status"
        aria-live="polite"
      >
        <div class="resource-performance-summary-header">
          <strong>{props.observation.status === "stale" ? uiText.misc.lastPerformanceSummary : uiText.misc.currentPerformanceSummary}</strong>
          <Show when={latest()?.capturedAt}>
            {(capturedAt) => <time datetime={capturedAt()}>{formatCapturedAt(capturedAt())}</time>}
          </Show>
        </div>
        <Show
          when={props.observation.status !== "error"
            && props.observation.status !== "profile-disabled"}
          fallback={<p>{props.observation.lastError ?? uiText.misc.performanceUnavailable}</p>}
        >
          <Show
            when={series().length > 0}
            fallback={<p>{uiText.misc.noPerformanceMetrics}</p>}
          >
            <Show when={latest()} fallback={<p>{uiText.misc.waitingFirstPerformance}</p>}>
              {(point) => (
                <dl class="resource-performance-summary-grid">
                  <For each={series()}>
                    {(item) => (
                      <div>
                        <dt>
                          <span
                            class="resource-performance-series-swatch"
                            style={{ "background-color": item.color }}
                            aria-hidden="true"
                          />
                          {item.label}
                        </dt>
                        <dd>{point().displays[item.id] ?? "--"}</dd>
                      </div>
                    )}
                  </For>
                </dl>
              )}
            </Show>
          </Show>
        </Show>
      </div>
    </div>
  );
}

export function defaultPerformanceMetricIds(catalog: MetricDefinition[]) {
  return performanceSeries(catalog).map((item) => item.id);
}

function performanceSeries(catalog: MetricDefinition[]): PerformanceSeries[] {
  const available = new Map(catalog.map((item) => [item.id, item]));
  const ids = [
    "cpu.usage",
    "memory.percent",
    "virtualMemory.percent",
    "disk.total.activePercent",
    "network.total.utilizationPercent",
    ...catalog
      .map((item) => item.id)
      .filter((id) => /^gpu\.\d+\.usage$/i.test(id) || /^gpu\.\d+\.vramPercent$/i.test(id))
      .sort(compareMetricIds)
  ];

  return ids
    .filter((id, index, all) => all.indexOf(id) === index)
    .filter((id) => available.has(id))
    .slice(0, 10)
    .map((id, index) => ({
      id,
      label: available.get(id)?.label ?? id,
      color: palette[index % palette.length]
    }));
}

function drawPerformanceCanvas(
  canvas: HTMLCanvasElement | undefined,
  series: PerformanceSeries[],
  history: PerformancePoint[])
{
  if (!canvas) {
    return;
  }

  const rect = canvas.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const width = Math.max(1, Math.floor(rect.width * dpr));
  const height = Math.max(1, Math.floor(rect.height * dpr));
  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }

  const context = canvas.getContext("2d");
  if (!context) {
    return;
  }

  context.save();
  context.scale(dpr, dpr);
  const cssWidth = width / dpr;
  const cssHeight = height / dpr;
  const theme = readCanvasTheme(canvas);
  context.clearRect(0, 0, cssWidth, cssHeight);
  context.fillStyle = theme.panel;
  context.fillRect(0, 0, cssWidth, cssHeight);

  const gap = 12;
  const columns = cssWidth >= 980 ? 2 : 1;
  const rows = Math.max(1, Math.ceil(series.length / columns));
  const cellWidth = (cssWidth - gap * (columns - 1)) / columns;
  const cellHeight = (cssHeight - gap * (rows - 1)) / rows;

  series.forEach((item, index) => {
    const column = index % columns;
    const row = Math.floor(index / columns);
    const x = column * (cellWidth + gap);
    const y = row * (cellHeight + gap);
    drawChart(context, x, y, cellWidth, cellHeight, item, history, theme);
  });

  context.restore();
}

function drawChart(
  context: CanvasRenderingContext2D,
  x: number,
  y: number,
  width: number,
  height: number,
  series: PerformanceSeries,
  history: PerformancePoint[],
  theme: PerformanceCanvasTheme)
{
  const paddingX = 10;
  const paddingTop = 46;
  const paddingBottom = 12;
  const chartX = x + paddingX;
  const chartY = y + paddingTop;
  const chartWidth = Math.max(1, width - paddingX * 2);
  const chartHeight = Math.max(1, height - paddingTop - paddingBottom);
  const latest = history[history.length - 1];

  context.fillStyle = theme.card;
  context.strokeStyle = theme.border;
  context.lineWidth = 1;
  roundRect(context, x + 0.5, y + 0.5, width - 1, height - 1, 8);
  context.fill();
  context.stroke();

  context.textBaseline = "top";
  context.fillStyle = theme.muted;
  context.font = "700 13px Segoe UI, Microsoft YaHei, sans-serif";
  context.fillText(series.label, x + 10, y + 12);
  context.fillStyle = theme.text;
  context.font = "800 24px Segoe UI, Microsoft YaHei, sans-serif";
  context.textAlign = "right";
  context.fillText(latest?.displays[series.id] ?? "--", x + width - 10, y + 6);
  context.textAlign = "left";
  context.textBaseline = "alphabetic";

  context.strokeStyle = theme.grid;
  context.lineWidth = 1;
  for (let i = 0; i <= 4; i++) {
    const yy = chartY + (chartHeight * i) / 4;
    context.beginPath();
    context.moveTo(chartX, yy);
    context.lineTo(chartX + chartWidth, yy);
    context.stroke();
  }
  for (let i = 0; i <= 8; i++) {
    const xx = chartX + (chartWidth * i) / 8;
    context.beginPath();
    context.moveTo(xx, chartY);
    context.lineTo(xx, chartY + chartHeight);
    context.stroke();
  }

  if (history.length === 0) {
    return;
  }

  if (history.length === 1) {
    const value = latest?.values[series.id];
    if (typeof value !== "number" || !Number.isFinite(value)) {
      return;
    }
    const levelY = chartY + chartHeight * (1 - value / 100);
    context.save();
    context.globalAlpha = 0.14;
    context.fillStyle = series.color;
    context.fillRect(
      chartX,
      levelY,
      chartWidth,
      chartY + chartHeight - levelY);
    context.restore();
    context.strokeStyle = series.color;
    context.lineWidth = 1.6;
    context.beginPath();
    context.moveTo(chartX, levelY);
    context.lineTo(chartX + chartWidth, levelY);
    context.stroke();
    context.fillStyle = series.color;
    context.beginPath();
    context.arc(chartX + chartWidth, levelY, 2.5, 0, Math.PI * 2);
    context.fill();
    return;
  }

  const pointDenominator = Math.max(1, history.length - 1);
  const runs = buildPerformanceRuns(history, series.id);
  for (const run of runs) {
    const points = run.map((point) => ({
      x: chartX + chartWidth * (point.index / pointDenominator),
      y: chartY + chartHeight * (1 - point.value / 100)
    }));
    if (points.length === 1) {
      context.fillStyle = series.color;
      context.beginPath();
      context.arc(points[0].x, points[0].y, 2, 0, Math.PI * 2);
      context.fill();
      continue;
    }

    context.beginPath();
    points.forEach((point, index) => {
      if (index === 0) {
        context.moveTo(point.x, point.y);
      } else {
        context.lineTo(point.x, point.y);
      }
    });
    context.lineTo(points.at(-1)!.x, chartY + chartHeight);
    context.lineTo(points[0].x, chartY + chartHeight);
    context.closePath();
    context.save();
    context.globalAlpha = 0.14;
    context.fillStyle = series.color;
    context.fill();
    context.restore();

    context.strokeStyle = series.color;
    context.lineWidth = 1.6;
    context.beginPath();
    points.forEach((point, index) => {
      if (index === 0) {
        context.moveTo(point.x, point.y);
      } else {
        context.lineTo(point.x, point.y);
      }
    });
    context.stroke();
  }
}

function readCanvasTheme(canvas: HTMLCanvasElement): PerformanceCanvasTheme {
  const style = getComputedStyle(canvas);
  const read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  return {
    panel: read("--surface-soft", "#fbfdfc"),
    card: read("--surface", "#ffffff"),
    border: read("--hairline", "#d8e0dd"),
    text: read("--text", "#0f1714"),
    muted: read("--muted", "#17201d"),
    grid: read("--line", "#eef2f0")
  };
}

function roundRect(context: CanvasRenderingContext2D, x: number, y: number, width: number, height: number, radius: number) {
  const safeRadius = Math.min(radius, width / 2, height / 2);
  context.beginPath();
  context.moveTo(x + safeRadius, y);
  context.arcTo(x + width, y, x + width, y + height, safeRadius);
  context.arcTo(x + width, y + height, x, y + height, safeRadius);
  context.arcTo(x, y + height, x, y, safeRadius);
  context.arcTo(x, y, x + width, y, safeRadius);
  context.closePath();
}

function compareMetricIds(left: string, right: string) {
  return metricOrder(left) - metricOrder(right) || left.localeCompare(right);
}

function metricOrder(id: string) {
  const match = /^gpu\.(\d+)\.(usage|vramPercent)$/i.exec(id);
  if (!match) {
    return 999;
  }

  return Number(match[1]) * 2 + (match[2].toLowerCase() === "vrampercent" ? 1 : 0);
}

function formatCapturedAt(value: string) {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return value;
  }
  return new Intl.DateTimeFormat(undefined, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  }).format(timestamp);
}
