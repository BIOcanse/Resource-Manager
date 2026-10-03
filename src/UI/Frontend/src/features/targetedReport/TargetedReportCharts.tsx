import { createEffect, onCleanup, onMount } from "solid-js";

export interface ChartPoint { x: number; y: number | null }

export function TargetedReportChart(props: {
  points: readonly ChartPoint[];
  from: number;
  to: number;
  mode: "frames" | "line" | "bars";
  ariaLabel: string;
  unit: string;
}) {
  let canvas: HTMLCanvasElement | undefined;
  let frame = 0;
  const drawSoon = () => {
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      draw(canvas, props.points, props.from, props.to, props.mode, props.unit);
    });
  };
  createEffect(() => {
    props.points;
    props.from;
    props.to;
    drawSoon();
  });
  onMount(() => {
    const observer = new ResizeObserver(drawSoon);
    const theme = new MutationObserver(drawSoon);
    if (canvas) observer.observe(canvas);
    theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    drawSoon();
    onCleanup(() => {
      observer.disconnect();
      theme.disconnect();
      if (frame) cancelAnimationFrame(frame);
    });
  });
  return <canvas ref={canvas} class="targeted-report-chart" role="img" aria-label={props.ariaLabel} />;
}

function draw(canvas: HTMLCanvasElement | undefined, points: readonly ChartPoint[], from: number,
  to: number, mode: "frames" | "line" | "bars", unit: string) {
  if (!canvas) return;
  const rect = canvas.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const width = Math.max(1, Math.floor(rect.width * dpr));
  const height = Math.max(1, Math.floor(rect.height * dpr));
  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }
  const context = canvas.getContext("2d");
  if (!context) return;
  const cssWidth = width / dpr;
  const cssHeight = height / dpr;
  const style = getComputedStyle(canvas);
  const color = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  const left = 48;
  const right = cssWidth - 12;
  const top = 12;
  const bottom = cssHeight - 27;
  const chartWidth = Math.max(1, right - left);
  const chartHeight = Math.max(1, bottom - top);
  let maximum = 1;
  for (const point of points) {
    if (point.y !== null && point.y > maximum) maximum = point.y;
  }
  const x = (value: number) => left + Math.max(0, Math.min(1, (value - from) / Math.max(1, to - from))) * chartWidth;
  const y = (value: number) => bottom - Math.max(0, Math.min(1, value / maximum)) * chartHeight;

  context.save();
  context.scale(dpr, dpr);
  context.clearRect(0, 0, cssWidth, cssHeight);
  context.fillStyle = color("--surface-soft", "#fbfdfc");
  context.fillRect(0, 0, cssWidth, cssHeight);
  context.strokeStyle = color("--line", "#e0e5e2");
  context.fillStyle = color("--muted", "#48534e");
  context.font = "11px Segoe UI, sans-serif";
  for (let index = 0; index <= 4; index++) {
    const level = maximum * index / 4;
    const yy = y(level);
    context.beginPath();
    context.moveTo(left, yy);
    context.lineTo(right, yy);
    context.stroke();
    context.fillText(formatAxis(level), 2, yy + 4);
  }
  context.fillText(unit, 3, 11);
  context.fillText(mode === "bars" ? String(from) : "0", left, cssHeight - 8);
  context.textAlign = "right";
  context.fillText(mode === "bars" ? String(to) : `${((to - from) / 1000).toFixed(0)} s`, right, cssHeight - 8);
  context.textAlign = "left";
  context.strokeStyle = color("--accent", "#0f8f7b");
  context.fillStyle = color("--accent", "#0f8f7b");
  context.lineWidth = 1.5;

  if (mode === "frames") {
    // Min/max envelope keeps long recordings at one draw operation per screen pixel.
    const minima = new Float64Array(Math.ceil(chartWidth)).fill(Number.POSITIVE_INFINITY);
    const maxima = new Float64Array(minima.length);
    for (const point of points) {
      if (point.y === null) continue;
      const bucket = Math.min(minima.length - 1, Math.max(0, Math.floor(x(point.x) - left)));
      minima[bucket] = Math.min(minima[bucket], point.y);
      maxima[bucket] = Math.max(maxima[bucket], point.y);
    }
    for (let bucket = 0; bucket < minima.length; bucket++) {
      if (!Number.isFinite(minima[bucket])) continue;
      context.beginPath();
      context.moveTo(left + bucket + 0.5, y(minima[bucket]));
      context.lineTo(left + bucket + 0.5, y(maxima[bucket]));
      context.stroke();
    }
  } else if (mode === "bars") {
    const barWidth = Math.max(1, chartWidth * 10 / Math.max(10, to - from) - 2);
    for (const point of points) {
      if (point.y === null) continue;
      context.fillRect(x(point.x), y(point.y), barWidth, bottom - y(point.y));
    }
  } else {
    let drawing = false;
    context.beginPath();
    for (const point of points) {
      if (point.y === null) {
        drawing = false;
        continue;
      }
      if (!drawing) context.moveTo(x(point.x), y(point.y));
      else context.lineTo(x(point.x), y(point.y));
      drawing = true;
    }
    context.stroke();
  }
  context.restore();
}

function formatAxis(value: number) {
  if (value >= 1_000_000_000) return `${(value / 1_000_000_000).toFixed(1)}G`;
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`;
  if (value >= 1_000) return `${(value / 1_000).toFixed(1)}k`;
  return value.toFixed(value >= 10 ? 0 : 1);
}
