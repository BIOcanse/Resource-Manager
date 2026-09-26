import { createEffect, createSignal, onCleanup } from "solid-js";
import { readDocumentTheme } from "../../presentation/documentTheme";
import type { ResourceSegmentCssVars } from "./resourceSegmentLayout";

export interface ResourcePaintSegment {
  key: string;
  left: number;
  right: number;
  color: string;
  sharedFraction?: number;
  distinctColor?: string;
  label?: string;
  labelColor?: string;
  distinctLabelColor?: string;
}

interface ResourceSegmentPaintPlaneProps {
  segments: ResourcePaintSegment[];
  logicalSegmentCount: number;
  segmentTop: number;
  segmentHeight: number;
  contentWidth: number;
  devicePixelRatio: number;
  drawTypeDividers?: boolean;
  moving?: boolean;
}

interface ResourceBarPresentation {
  animations: string;
  barColor: string;
  theme: string;
  selfGpuGrade: string;
  frontendFocused: string;
}

interface PaintGeometry {
  width: number;
  height: number;
  devicePixelRatio: number;
  overscan: number;
}

interface PaintOptions {
  drawDividers: boolean;
}

const animationDurationMs = 360;
const paintOverscanPx = 4;
const dividerColor = "var(--divider)";

export function ResourceSegmentPaintPlane(props: ResourceSegmentPaintPlaneProps) {
  let canvas: HTMLCanvasElement | undefined;
  let animationFrame = 0;
  let lastSignature = "";
  let lastGeometrySignature = "";
  let lastDrawnSegments: ResourcePaintSegment[] = [];
  const [presentation, setPresentation] = createSignal(readResourceBarPresentation());

  const observer = new MutationObserver(() => setPresentation(readResourceBarPresentation()));
  observer.observe(document.body, {
    attributes: true,
    attributeFilter: ["data-animations", "data-bar-color", "data-self-gpu-grade", "data-frontend-focused"]
  });
  observer.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-theme"]
  });

  onCleanup(() => {
    observer.disconnect();
    cancelAnimationFrame(animationFrame);
  });

  createEffect(() => {
    const element = canvas;
    const currentPresentation = presentation();
    const moving = Boolean(props.moving);
    const targetSegments = normalizePaintSegments(props.segments);
    const geometry = normalizePaintGeometry(props.contentWidth, props.segmentHeight, props.devicePixelRatio, paintOverscanPx);
    const paintOptions = {
      drawDividers: shouldDrawTypeDividers(targetSegments, currentPresentation, Boolean(props.drawTypeDividers), moving)
    };
    const signature = paintSegmentSignature(targetSegments);
    const geometrySignature = `${geometry.width}|${geometry.height}|${geometry.devicePixelRatio}|${geometry.overscan}`;
    const shouldAnimate = Boolean(lastSignature)
      && signature !== lastSignature
      && geometrySignature === lastGeometrySignature
      && canAnimateResourceBar(currentPresentation);

    lastSignature = signature;
    lastGeometrySignature = geometrySignature;

    if (!element || geometry.width <= 0 || geometry.height <= 0) {
      return;
    }

    cancelAnimationFrame(animationFrame);

    if (!shouldAnimate || lastDrawnSegments.length === 0) {
      drawSegments(element, targetSegments, geometry, currentPresentation, paintOptions);
      lastDrawnSegments = targetSegments;
      return;
    }

    animateSegments(element, lastDrawnSegments, targetSegments, geometry, currentPresentation, paintOptions, (frame) => {
      animationFrame = frame;
    }, (drawn) => {
      lastDrawnSegments = drawn;
    });
  });

  const style = () => ({
    "--segment-render-top": `${Math.max(0, Number(props.segmentTop) || 0)}px`,
    "--segment-render-height": `${Math.max(0, Number(props.segmentHeight) || 0)}px`,
    "--segment-paint-overscan": `${paintOverscanPx}px`
  }) satisfies ResourceSegmentCssVars;

  return (
    <div
      class="resource-segment-visual-plane"
      aria-hidden="true"
      style={style()}
    >
      <canvas
        class="resource-segment-canvas"
        data-resource-paint-plane
        data-resource-segment-count={props.logicalSegmentCount}
        ref={(element) => { canvas = element; }}
      />
    </div>
  );
}

function animateSegments(
  canvas: HTMLCanvasElement,
  fromSegments: ResourcePaintSegment[],
  toSegments: ResourcePaintSegment[],
  geometry: PaintGeometry,
  presentation: ResourceBarPresentation,
  paintOptions: PaintOptions,
  setAnimationFrame: (frame: number) => void,
  setLastDrawnSegments: (segments: ResourcePaintSegment[]) => void)
{
  const fromByKey = new Map(fromSegments.map((segment) => [segment.key, segment]));
  const startedAt = performance.now();

  const step = (now: number) => {
    const progress = clampNumber((now - startedAt) / animationDurationMs, 0, 1);
    const eased = easeBar(progress);
    const interpolated = toSegments.map((target) => {
      const source = fromByKey.get(target.key) ?? target;
      return {
        ...target,
        left: lerpNumber(source.left, target.left, eased),
        right: lerpNumber(source.right, target.right, eased)
      };
    });

    drawSegments(canvas, interpolated, geometry, presentation, { drawDividers: false });
    setLastDrawnSegments(interpolated);

    if (progress < 1) {
      setAnimationFrame(requestAnimationFrame(step));
      return;
    }

    drawSegments(canvas, toSegments, geometry, presentation, paintOptions);
    setLastDrawnSegments(toSegments);
  };

  setAnimationFrame(requestAnimationFrame(step));
}

function drawSegments(
  canvas: HTMLCanvasElement,
  segments: ResourcePaintSegment[],
  geometry: PaintGeometry,
  presentation: ResourceBarPresentation,
  paintOptions: PaintOptions)
{
  const pixelWidth = Math.max(1, Math.round(geometry.width * geometry.devicePixelRatio));
  const paintHeight = geometry.height + geometry.overscan * 2;
  const pixelHeight = Math.max(1, Math.round(paintHeight * geometry.devicePixelRatio));
  if (canvas.width !== pixelWidth) {
    canvas.width = pixelWidth;
  }
  if (canvas.height !== pixelHeight) {
    canvas.height = pixelHeight;
  }

  const context = canvas.getContext("2d", { alpha: true });
  if (!context) {
    return;
  }

  const width = pixelWidth / geometry.devicePixelRatio;
  const height = pixelHeight / geometry.devicePixelRatio;
  context.setTransform(geometry.devicePixelRatio, 0, 0, geometry.devicePixelRatio, 0, 0);
  context.clearRect(0, 0, width, height);
  context.imageSmoothingEnabled = false;

  for (const segment of segments) {
    const leftPx = Math.floor(clampNumber(segment.left, 0, 100) * pixelWidth / 100);
    const rightPx = Math.floor(clampNumber(segment.right, 0, 100) * pixelWidth / 100);
    if (rightPx <= leftPx) {
      continue;
    }

    context.fillStyle = resolveCanvasColor(segmentPaintColor(segment, presentation), canvas);
    context.fillRect(leftPx / geometry.devicePixelRatio, 0, (rightPx - leftPx) / geometry.devicePixelRatio, height);
    if (segment.sharedFraction) {
      const sharedLeft = Math.floor(rightPx - (rightPx - leftPx) * segment.sharedFraction);
      context.fillStyle = "rgb(211 171 48)";
      context.fillRect(sharedLeft / geometry.devicePixelRatio, 0, (rightPx - sharedLeft) / geometry.devicePixelRatio, height);
    }
  }

  if (paintOptions.drawDividers) {
    drawSegmentDividers(context, canvas, segments, geometry, pixelWidth, pixelHeight);
  }

  drawSegmentLabels(context, canvas, segments, presentation, geometry, pixelWidth, pixelHeight);
}

function drawSegmentLabels(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  segments: ResourcePaintSegment[],
  presentation: ResourceBarPresentation,
  geometry: PaintGeometry,
  pixelWidth: number,
  pixelHeight: number)
{
  const devicePixelRatio = geometry.devicePixelRatio;
  const style = getComputedStyle(canvas);
  const fontFamily = style.fontFamily || "Segoe UI, sans-serif";
  context.font = `700 12px ${fontFamily}`;
  context.textAlign = "center";
  context.textBaseline = "middle";

  for (const segment of segments) {
    if (!segment.label) {
      continue;
    }

    const leftPx = Math.floor(clampNumber(segment.left, 0, 100) * pixelWidth / 100);
    const rightPx = Math.floor(clampNumber(segment.right, 0, 100) * pixelWidth / 100);
    const width = (rightPx - leftPx) / devicePixelRatio;
    const availableWidth = width - 12;
    if (availableWidth < 20) {
      continue;
    }

    const label = fitCanvasLabel(context, segment.label, availableWidth);
    if (!label) {
      continue;
    }

    context.save();
    context.beginPath();
    context.rect(
      leftPx / devicePixelRatio,
      0,
      width,
      pixelHeight / devicePixelRatio);
    context.clip();
    context.fillStyle = resolveCanvasColor(segmentPaintLabelColor(segment, presentation), canvas);
    context.fillText(
      label,
      (leftPx + rightPx) / (2 * devicePixelRatio),
      pixelHeight / (2 * devicePixelRatio));
    context.restore();
  }
}

function fitCanvasLabel(
  context: CanvasRenderingContext2D,
  label: string,
  availableWidth: number)
{
  if (context.measureText(label).width <= availableWidth) {
    return label;
  }

  const suffix = "...";
  if (context.measureText(suffix).width > availableWidth) {
    return "";
  }

  let low = 0;
  let high = label.length;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    const candidate = `${label.slice(0, middle)}${suffix}`;
    if (context.measureText(candidate).width <= availableWidth) {
      low = middle;
    } else {
      high = middle - 1;
    }
  }

  return `${label.slice(0, low)}${suffix}`;
}

function drawSegmentDividers(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  segments: ResourcePaintSegment[],
  geometry: PaintGeometry,
  pixelWidth: number,
  pixelHeight: number)
{
  const devicePixelRatio = geometry.devicePixelRatio;
  const lineWidthPx = 1;
  const height = pixelHeight / devicePixelRatio;
  const drawnBoundaries = new Set<number>();

  context.fillStyle = resolveCanvasColor(dividerColor, canvas);

  for (let index = 0; index < segments.length - 1; index++) {
    const boundaryPx = Math.floor(clampNumber(segments[index].right, 0, 100) * pixelWidth / 100);
    if (boundaryPx <= 0 || boundaryPx >= pixelWidth || drawnBoundaries.has(boundaryPx)) {
      continue;
    }

    drawnBoundaries.add(boundaryPx);
    const leftPx = clampNumber(boundaryPx - lineWidthPx, 0, pixelWidth - lineWidthPx);
    context.fillRect(leftPx / devicePixelRatio, 0, lineWidthPx / devicePixelRatio, height);
  }
}

function normalizePaintSegments(segments: ResourcePaintSegment[]) {
  return segments
    .map((segment) => ({
      ...segment,
      left: clampNumber(Number(segment.left) || 0, 0, 100),
      right: clampNumber(Number(segment.right) || 0, 0, 100)
    }))
    .filter((segment) => segment.key && segment.right > segment.left);
}

function normalizePaintGeometry(width: number, height: number, devicePixelRatio: number, overscan: number): PaintGeometry {
  return {
    width: Math.max(0, Number(width) || 0),
    height: Math.max(0, Number(height) || 0),
    devicePixelRatio: Math.max(1, Number(devicePixelRatio) || 1),
    overscan: Math.max(0, Number(overscan) || 0)
  };
}

function paintSegmentSignature(segments: ResourcePaintSegment[]) {
  return segments
    .map((segment) => `${segment.key}:${segment.left.toFixed(4)}:${segment.right.toFixed(4)}:${segment.color}:${segment.distinctColor ?? ""}:${segment.sharedFraction ?? 0}`)
    .join("|");
}

function segmentPaintColor(segment: ResourcePaintSegment, presentation: ResourceBarPresentation) {
  return presentation.barColor === "distinct"
    ? segment.distinctColor ?? segment.color
    : segment.color;
}

function segmentPaintLabelColor(segment: ResourcePaintSegment, presentation: ResourceBarPresentation) {
  return presentation.barColor === "distinct"
    ? segment.distinctLabelColor ?? segment.labelColor ?? "var(--text)"
    : segment.labelColor ?? "var(--text)";
}

function shouldDrawTypeDividers(
  segments: ResourcePaintSegment[],
  presentation: ResourceBarPresentation,
  drawTypeDividers: boolean,
  moving: boolean)
{
  return drawTypeDividers
    && !moving
    && presentation.barColor !== "distinct"
    && segments.length > 1;
}

function resolveCanvasColor(color: string, element: HTMLElement) {
  const trimmed = color.trim();
  if (!trimmed.startsWith("var(")) {
    return toCanvasSupportedColor(trimmed);
  }

  const match = /^var\((--[\w-]+)(?:,\s*(.+))?\)$/.exec(trimmed);
  if (!match) {
    return trimmed;
  }

  const resolved = getComputedStyle(element).getPropertyValue(match[1]).trim();
  const fallback = match[2]?.trim() ?? trimmed;
  return toCanvasSupportedColor(resolved || fallback);
}

function toCanvasSupportedColor(color: string) {
  const match = /^color\(display-p3\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)(?:\s*\/\s*([\d.]+))?\)$/i.exec(color.trim());
  if (!match) {
    return color;
  }

  const red = Math.floor(clampNumber(Number(match[1]) || 0, 0, 1) * 255);
  const green = Math.floor(clampNumber(Number(match[2]) || 0, 0, 1) * 255);
  const blue = Math.floor(clampNumber(Number(match[3]) || 0, 0, 1) * 255);
  const alpha = match[4] === undefined ? 1 : clampNumber(Number(match[4]) || 0, 0, 1);
  return `rgba(${red}, ${green}, ${blue}, ${alpha})`;
}

function readResourceBarPresentation(): ResourceBarPresentation {
  return {
    animations: document.body.dataset.animations ?? "normal",
    barColor: document.body.dataset.barColor ?? "type",
    theme: readDocumentTheme(),
    selfGpuGrade: document.body.dataset.selfGpuGrade ?? "normal",
    frontendFocused: document.body.dataset.frontendFocused ?? "yes"
  };
}

function canAnimateResourceBar(presentation: ResourceBarPresentation) {
  if (presentation.animations !== "normal") {
    return false;
  }

  return presentation.selfGpuGrade !== "optimize"
    || presentation.frontendFocused === "yes";
}

function easeBar(progress: number) {
  return progress * progress * (3 - 2 * progress);
}

function lerpNumber(from: number, to: number, progress: number) {
  return from + (to - from) * progress;
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
