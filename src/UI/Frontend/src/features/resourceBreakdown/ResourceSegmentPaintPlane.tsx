import { canAnimateResourceBar } from "./resourceBarAnimation.ts";
import { createEffect, createSignal, onCleanup } from "solid-js";
import { observeDocumentAppearance } from "../../presentation/documentAppearance";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds.ts";
import { useWhileNeeded } from "../../frontendWork/useWhileNeeded.ts";
import {
  parseResourceBarFrameRate,
  shouldDrawResourceBarFrame,
  type ResourceBarFrameRate
} from "../../presentation/resourceBarFrameRate";
import type { ResourceSegmentCssVars } from "./resourceSegmentLayout";
import { readMotionDurationMs, readMotionEase } from "../../presentation/motionTokens.ts";
import {
  enteringSources,
  fitCanvasLabel,
  leavingSegments,
  transitionFrame,
  type ResourcePaintSegment
} from "./resourceSegmentPainting.ts";

export type { ResourcePaintSegment } from "./resourceSegmentPainting.ts";


interface ResourceSegmentPaintPlaneProps {
  segments: ResourcePaintSegment[];
  logicalSegmentCount: number;
  segmentTop: number;
  segmentHeight: number;
  contentWidth: number;
  devicePixelRatio: number;
  drawDividers?: boolean;
}

/** Appearance includes animation, frame rate, and all style inputs affecting canvas colors. */
interface ResourceBarPresentation {
  animations: string;
  barColor: string;
  frameRate: ResourceBarFrameRate;
  uiStyle: string;
  theme: string;
  systemDark: boolean;
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

const paintOverscanPx = 4;
const dividerColor = "var(--divider)";
const sharedTintColor = "var(--resource-shared-tint)";
const hatchStripeColor = "var(--resource-reserved-stripe)";
const hatchSpacingPx = 6;
// Omit separators for segments already thinner than a device pixel.
const sliverDevicePx = 3;
// Omit labels that cannot retain the minimum meaningful prefix.
const minimumLabelCharacters = 3;

export function ResourceSegmentPaintPlane(props: ResourceSegmentPaintPlaneProps) {
  let canvas: HTMLCanvasElement | undefined;
  let animationFrame = 0;
  let lastSignature = "";
  let lastGeometrySignature = "";
  let lastPresentation: ResourceBarPresentation | null = null;
  let lastDrawnSegments: ResourcePaintSegment[] = [];
  // Reuse canvas styles while appearance is unchanged; do not reread styles or measure text on each data update.
  let paintStyle: { presentation: ResourceBarPresentation; style: CanvasPaintStyle } | null = null;
  // Compare appearance by value because attribute observers may report identical writes.
  // An unchanged appearance must not finish an active transition.
  const [presentation, setPresentation] = createSignal(readResourceBarPresentation(), { equals: samePresentation });

  // Refresh appearance on entry and observe changes only while needed.
  const refreshPresentation = () => setPresentation(readResourceBarPresentation());
  useWhileNeeded(frontendWorkIds.monitorBars, () => {
    refreshPresentation();
    const observer = new MutationObserver(refreshPresentation);
    observer.observe(document.body, {
      attributes: true,
      attributeFilter: ["data-animations", "data-bar-color", "data-resource-bar-frame-rate"]
    });
    const stopObservingAppearance = observeDocumentAppearance(refreshPresentation);
    return () => {
      observer.disconnect();
      stopObservingAppearance();
    };
  });

  onCleanup(() => {
    cancelAnimationFrame(animationFrame);
  });

  createEffect(() => {
    const element = canvas;
    const currentPresentation = presentation();
    const targetSegments = normalizePaintSegments(props.segments);
    const geometry = normalizePaintGeometry(props.contentWidth, props.segmentHeight, props.devicePixelRatio, paintOverscanPx);
    const paintOptions = {
      drawDividers: shouldDrawDividers(targetSegments, Boolean(props.drawDividers))
    };
    const signature = paintSegmentSignature(targetSegments);
    const geometrySignature = `${geometry.width}|${geometry.height}|${geometry.devicePixelRatio}|${geometry.overscan}`;
    const unchanged = signature === lastSignature
      && geometrySignature === lastGeometrySignature
      && currentPresentation === lastPresentation;
    const sizeChanged = geometrySignature !== lastGeometrySignature;
    const shouldAnimate = Boolean(lastSignature)
      && signature !== lastSignature
      && !sizeChanged
      && canAnimateResourceBar(currentPresentation.animations);

    lastSignature = signature;
    lastGeometrySignature = geometrySignature;
    lastPresentation = currentPresentation;

    if (!element || geometry.width <= 0 || geometry.height <= 0) {
      return;
    }
    // Skip repainting when segments, dimensions, and appearance match the current target.
    // Let any active transition finish normally.
    if (unchanged) {
      return;
    }

    cancelAnimationFrame(animationFrame);
    animationFrame = 0;
    if (paintStyle?.presentation !== currentPresentation) {
      paintStyle = { presentation: currentPresentation, style: createCanvasPaintStyle(element) };
    }
    const style = paintStyle.style;

    // Paint dimension changes immediately to avoid displaying a stretched old image.
    if (sizeChanged) {
      drawSegments(element, targetSegments, geometry, currentPresentation, paintOptions, style);
      lastDrawnSegments = targetSegments;
      return;
    }
    // Defer same-size painting to the next frame,
    // avoiding forced style recalculation immediately after data changes.
    if (!shouldAnimate || lastDrawnSegments.length === 0) {
      animationFrame = requestAnimationFrame(() => {
        animationFrame = 0;
        drawSegments(element, targetSegments, geometry, currentPresentation, paintOptions, style);
        lastDrawnSegments = targetSegments;
      });
      return;
    }

    animateSegments(element, lastDrawnSegments, targetSegments, geometry, currentPresentation, paintOptions, style, (frame) => {
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
  paintStyle: CanvasPaintStyle,
  setAnimationFrame: (frame: number) => void,
  setLastDrawnSegments: (segments: ResourcePaintSegment[]) => void)
{
  // Grow entering segments from the nearest surviving left neighbor's old edge to avoid gaps.
  const fromByKey = new Map([
    ...enteringSources(fromSegments, toSegments),
    ...fromSegments.map((segment) => [segment.key, segment] as const)
  ]);
  const leaving = leavingSegments(fromSegments, toSegments);
  // Read shared duration and easing once per transition; zero duration paints the final frame.
  const durationMs = readMotionDurationMs(canvas, "--motion-bar");
  const easeBar = readMotionEase(canvas, "--ease-bar");
  const startedAt = performance.now();
  const pixelWidth = paintPixelWidth(geometry);
  // Skip intermediate frames when rounded geometry already matches the target.
  if (durationMs <= 0 || leaving.length === 0
    && segmentPixelKey(toSegments.map((target) => fromByKey.get(target.key) ?? target), pixelWidth)
      === segmentPixelKey(toSegments, pixelWidth)) {
    setAnimationFrame(requestAnimationFrame(() => {
      drawSegments(canvas, toSegments, geometry, presentation, paintOptions, paintStyle);
      setLastDrawnSegments(toSegments);
      setAnimationFrame(0);
    }));
    return;
  }
  let drawnPixelKey: string | null = null;
  let drawnAt: number | null = null;

  const step = (now: number) => {
    const progress = clampNumber((now - startedAt) / durationMs, 0, 1);
    // Respect custom frame rate without advancing the last-painted geometry on skipped frames.
    if (progress < 1 && !shouldDrawResourceBarFrame(presentation.frameRate, drawnAt, now)) {
      setAnimationFrame(requestAnimationFrame(step));
      return;
    }
    const eased = easeBar(progress);
    // Paint departing segments underneath; neighboring movement reveals entering segments.
    const interpolated = transitionFrame(fromByKey, leaving, toSegments, eased);

    if (progress < 1) {
      // Skip drawing frames with unchanged pixel geometry.
      const pixelKey = segmentPixelKey(interpolated, pixelWidth);
      if (pixelKey !== drawnPixelKey) {
        // Draw separators throughout transitions to prevent flicker.
        drawSegments(canvas, interpolated, geometry, presentation, paintOptions, paintStyle);
        drawnPixelKey = pixelKey;
        drawnAt = now;
      }
      setLastDrawnSegments(interpolated);
      setAnimationFrame(requestAnimationFrame(step));
      return;
    }

    drawSegments(canvas, toSegments, geometry, presentation, paintOptions, paintStyle);
    setLastDrawnSegments(toSegments);
    setAnimationFrame(0);
  };

  setAnimationFrame(requestAnimationFrame(step));
}

function drawSegments(
  canvas: HTMLCanvasElement,
  segments: ResourcePaintSegment[],
  geometry: PaintGeometry,
  presentation: ResourceBarPresentation,
  paintOptions: PaintOptions,
  paintStyle: CanvasPaintStyle)
{
  const pixelWidth = paintPixelWidth(geometry);
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

  // Paint subpixel segments last at one device pixel so later segments cannot cover them.
  const slivers: [number, ResourcePaintSegment][] = [];
  for (const segment of segments) {
    const leftPx = Math.floor(clampNumber(segment.left, 0, 100) * pixelWidth / 100);
    const rightPx = Math.floor(clampNumber(segment.right, 0, 100) * pixelWidth / 100);
    if (rightPx <= leftPx) {
      slivers.push([Math.min(leftPx, pixelWidth - 1), segment]);
      continue;
    }
    paintSegmentBody(context, segment, leftPx, rightPx, height, geometry.devicePixelRatio, presentation, paintStyle);
  }
  for (const [leftPx, segment] of slivers) {
    paintSegmentBody(context, segment, leftPx, leftPx + 1, height, geometry.devicePixelRatio, presentation, paintStyle);
  }

  if (paintOptions.drawDividers) {
    drawSegmentDividers(context, segments, geometry, pixelWidth, pixelHeight, paintStyle);
  }

  drawSegmentLabels(context, segments, geometry, pixelWidth, pixelHeight, paintStyle);
}

function paintSegmentBody(
  context: CanvasRenderingContext2D,
  segment: ResourcePaintSegment,
  leftPx: number,
  rightPx: number,
  height: number,
  devicePixelRatio: number,
  presentation: ResourceBarPresentation,
  paintStyle: CanvasPaintStyle)
{
  const left = leftPx / devicePixelRatio;
  const width = (rightPx - leftPx) / devicePixelRatio;
  context.fillStyle = paintStyle.color(segmentPaintColor(segment, presentation));
  context.fillRect(left, 0, width, height);
  if (segment.hatched) {
    context.save();
    context.beginPath();
    context.rect(left, 0, width, height);
    context.clip();
    context.strokeStyle = paintStyle.color(hatchStripeColor);
    context.lineWidth = 2;
    context.beginPath();
    // Anchor hatch spacing to the track so width changes do not slide the pattern.
    let x = Math.floor((left - height) / hatchSpacingPx) * hatchSpacingPx;
    while (x < left + width) {
      context.moveTo(x, height);
      context.lineTo(x + height, 0);
      x += hatchSpacingPx;
    }
    context.stroke();
    context.restore();
  }
  if (segment.sharedFraction) {
    const sharedLeftPx = Math.floor(rightPx - (rightPx - leftPx) * segment.sharedFraction);
    context.fillStyle = paintStyle.color(sharedTintColor);
    context.fillRect(sharedLeftPx / devicePixelRatio, 0, (rightPx - sharedLeftPx) / devicePixelRatio, height);
  }
}

function drawSegmentLabels(
  context: CanvasRenderingContext2D,
  segments: ResourcePaintSegment[],
  geometry: PaintGeometry,
  pixelWidth: number,
  pixelHeight: number,
  paintStyle: CanvasPaintStyle)
{
  const devicePixelRatio = geometry.devicePixelRatio;
  context.font = paintStyle.font();
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

    const label = fitCanvasLabel((text) => paintStyle.textWidth(context, text), segment.label, availableWidth, minimumLabelCharacters);
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
    context.fillStyle = paintStyle.color(segmentPaintLabelColor(segment));
    context.fillText(
      label,
      (leftPx + rightPx) / (2 * devicePixelRatio),
      pixelHeight / (2 * devicePixelRatio));
    context.restore();
  }
}

function drawSegmentDividers(
  context: CanvasRenderingContext2D,
  segments: ResourcePaintSegment[],
  geometry: PaintGeometry,
  pixelWidth: number,
  pixelHeight: number,
  paintStyle: CanvasPaintStyle)
{
  const devicePixelRatio = geometry.devicePixelRatio;
  const lineWidthPx = 1;
  const height = pixelHeight / devicePixelRatio;
  const drawnBoundaries = new Set<number>();

  context.fillStyle = paintStyle.color(dividerColor);

  const devicePxWidth = (segment: ResourcePaintSegment) =>
    Math.floor(clampNumber(segment.right, 0, 100) * pixelWidth / 100) - Math.floor(clampNumber(segment.left, 0, 100) * pixelWidth / 100);
  for (let index = 0; index < segments.length - 1; index++) {
    const boundaryPx = Math.floor(clampNumber(segments[index].right, 0, 100) * pixelWidth / 100);
    if (boundaryPx <= 0 || boundaryPx >= pixelWidth || drawnBoundaries.has(boundaryPx)
      || devicePxWidth(segments[index]) < sliverDevicePx || devicePxWidth(segments[index + 1]) < sliverDevicePx) {
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

function paintPixelWidth(geometry: PaintGeometry) {
  return Math.max(1, Math.round(geometry.width * geometry.devicePixelRatio));
}

/** Key rounded segment boundaries; colors and labels remain fixed during a transition. */
function segmentPixelKey(segments: ResourcePaintSegment[], pixelWidth: number) {
  let key = "";
  for (const segment of segments) {
    key += `${Math.floor(clampNumber(segment.left, 0, 100) * pixelWidth / 100)},${Math.floor(clampNumber(segment.right, 0, 100) * pixelWidth / 100)};`;
  }
  return key;
}

function paintSegmentSignature(segments: ResourcePaintSegment[]) {
  return segments
    .map((segment) => `${segment.key}:${segment.left.toFixed(4)}:${segment.right.toFixed(4)}:${segment.color}:${segment.distinctColor ?? ""}:${segment.sharedFraction ?? 0}:${segment.hatched ? 1 : 0}:${segment.label ?? ""}`)
    .join("|");
}

function segmentPaintColor(segment: ResourcePaintSegment, presentation: ResourceBarPresentation) {
  return presentation.barColor === "distinct"
    ? segment.distinctColor ?? segment.color
    : segment.color;
}

function segmentPaintLabelColor(segment: ResourcePaintSegment) {
  return segment.labelColor ?? "var(--text)";
}

// Draw separators in both palettes because adjacent colors may match.
function shouldDrawDividers(
  segments: ResourcePaintSegment[],
  drawDividers: boolean)
{
  return drawDividers && segments.length > 1;
}

/** Cache resolved styles and bounded text measurements per appearance. Read computed styles lazily and clear text measurements at capacity. */
interface CanvasPaintStyle {
  font(): string;
  color(value: string): string;
  textWidth(context: CanvasRenderingContext2D, text: string): number;
}

const maxMeasuredTexts = 8192;

function createCanvasPaintStyle(canvas: HTMLCanvasElement): CanvasPaintStyle {
  let computed: CSSStyleDeclaration | null = null;
  let font: string | null = null;
  const colors = new Map<string, string>();
  const widths = new Map<string, number>();
  const style = () => computed ??= getComputedStyle(canvas);
  return {
    font: () => font ??= `700 12px ${style().fontFamily || "Segoe UI, sans-serif"}`,
    color: (value) => {
      let resolved = colors.get(value);
      if (resolved === undefined) {
        resolved = resolveCanvasColor(value, style);
        colors.set(value, resolved);
      }
      return resolved;
    },
    textWidth: (context, text) => {
      let width = widths.get(text);
      if (width === undefined) {
        if (widths.size >= maxMeasuredTexts) {
          widths.clear();
        }
        width = context.measureText(text).width;
        widths.set(text, width);
      }
      return width;
    }
  };
}

function resolveCanvasColor(color: string, style: () => CSSStyleDeclaration) {
  const trimmed = color.trim();
  if (!trimmed.startsWith("var(")) {
    return toCanvasSupportedColor(trimmed);
  }

  const match = /^var\((--[\w-]+)(?:,\s*(.+))?\)$/.exec(trimmed);
  if (!match) {
    return trimmed;
  }

  const resolved = style().getPropertyValue(match[1]).trim();
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
    animations: document.body.dataset.animations ?? "",
    barColor: document.body.dataset.barColor ?? "type",
    frameRate: parseResourceBarFrameRate(document.body.dataset.resourceBarFrameRate),
    uiStyle: document.documentElement.dataset.uiStyle ?? "",
    theme: document.documentElement.dataset.theme ?? "",
    systemDark: window.matchMedia?.("(prefers-color-scheme: dark)").matches === true
  };
}

function samePresentation(a: ResourceBarPresentation, b: ResourceBarPresentation) {
  return a.animations === b.animations
    && a.barColor === b.barColor
    && a.frameRate === b.frameRate
    && a.uiStyle === b.uiStyle
    && a.theme === b.theme
    && a.systemDark === b.systemDark;
}



function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
