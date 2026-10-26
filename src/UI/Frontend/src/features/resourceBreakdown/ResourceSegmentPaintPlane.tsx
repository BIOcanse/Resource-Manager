import { canAnimateResourceBar } from "./resourceBarAnimation.ts";
import { createEffect, createSignal, onCleanup } from "solid-js";
import { observeDocumentAppearance } from "../../presentation/documentAppearance";
import {
  parseResourceBarFrameRate,
  shouldDrawResourceBarFrame,
  type ResourceBarFrameRate
} from "../../presentation/resourceBarFrameRate";
import type { ResourceSegmentCssVars } from "./resourceSegmentLayout";
import { readMotionDurationMs, readMotionEase } from "../../presentation/motionTokens.ts";
import {
  enteringSources,
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

/** 画布画法依赖的外观：动画模式、配色、帧率，以及决定 CSS 颜色变量取值的风格、配色主题和系统深浅色。 */
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

export function ResourceSegmentPaintPlane(props: ResourceSegmentPaintPlaneProps) {
  let canvas: HTMLCanvasElement | undefined;
  let animationFrame = 0;
  let lastSignature = "";
  let lastGeometrySignature = "";
  let lastPresentation: ResourceBarPresentation | null = null;
  let lastDrawnSegments: ResourcePaintSegment[] = [];
  // 按内容比较：属性被写成同样的值时（App 的外观副作用随自调度快照重跑，每次都会写）观察器也会通知，
  // 这时外观没变，不能当成变化去重画，否则正在走的过渡被直接画成末帧。
  const [presentation, setPresentation] = createSignal(readResourceBarPresentation(), { equals: samePresentation });

  const refreshPresentation = () => setPresentation(readResourceBarPresentation());
  const observer = new MutationObserver(refreshPresentation);
  observer.observe(document.body, {
    attributes: true,
    attributeFilter: ["data-animations", "data-bar-color", "data-resource-bar-frame-rate"]
  });
  const stopObservingAppearance = observeDocumentAppearance(refreshPresentation);

  onCleanup(() => {
    observer.disconnect();
    stopObservingAppearance();
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
    const shouldAnimate = Boolean(lastSignature)
      && signature !== lastSignature
      && geometrySignature === lastGeometrySignature
      && canAnimateResourceBar(currentPresentation.animations);

    lastSignature = signature;
    lastGeometrySignature = geometrySignature;
    lastPresentation = currentPresentation;

    if (!element || geometry.width <= 0 || geometry.height <= 0) {
      return;
    }
    // 要画的和正在过渡去的完全一样（别的依赖变了但段、尺寸、外观都没变）：让过渡走完，不跳到末帧。
    if (unchanged && animationFrame !== 0) {
      return;
    }

    cancelAnimationFrame(animationFrame);
    animationFrame = 0;
    const paintStyle = createCanvasPaintStyle(element);

    if (!shouldAnimate || lastDrawnSegments.length === 0) {
      drawSegments(element, targetSegments, geometry, currentPresentation, paintOptions, paintStyle);
      lastDrawnSegments = targetSegments;
      return;
    }

    animateSegments(element, lastDrawnSegments, targetSegments, geometry, currentPresentation, paintOptions, paintStyle, (frame) => {
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
  // 新来的段从左边最近仍在的段的旧右端长出来，和离开的段一样不让条上露底。
  const fromByKey = new Map([
    ...enteringSources(fromSegments, toSegments),
    ...fromSegments.map((segment) => [segment.key, segment] as const)
  ]);
  const leaving = leavingSegments(fromSegments, toSegments);
  // 时长和曲线与上面交互层的数据条过渡（motion.css）是同一对令牌，这一段开始时读一次；时长 0 时直接画末帧。
  const durationMs = readMotionDurationMs(canvas, "--motion-bar");
  const easeBar = readMotionEase(canvas, "--ease-bar");
  const startedAt = performance.now();
  const pixelWidth = paintPixelWidth(geometry);
  // 每秒一次的小幅变化常常落在同样的像素上：这时中间帧与末帧逐像素相同，直接画末帧。
  if (durationMs <= 0 || leaving.length === 0
    && segmentPixelKey(toSegments.map((target) => fromByKey.get(target.key) ?? target), pixelWidth)
      === segmentPixelKey(toSegments, pixelWidth)) {
    drawSegments(canvas, toSegments, geometry, presentation, paintOptions, paintStyle);
    setLastDrawnSegments(toSegments);
    return;
  }
  let drawnPixelKey: string | null = null;
  let drawnAt: number | null = null;

  const step = (now: number) => {
    const progress = clampNumber((now - startedAt) / durationMs, 0, 1);
    // 自选帧率时没到点的中间帧整帧跳过；画面和 lastDrawnSegments 都停在上次画出的那一帧。
    if (progress < 1 && !shouldDrawResourceBarFrame(presentation.frameRate, drawnAt, now)) {
      setAnimationFrame(requestAnimationFrame(step));
      return;
    }
    const eased = easeBar(progress);
    // 离开的段先画、垫在下面；新来的段直接在终点，由右边让开的段逐渐露出来。
    const interpolated = transitionFrame(fromByKey, leaving, toSegments, eased);

    if (progress < 1) {
      // 与上一帧落在同样的像素上时不重画，画面不变。
      const pixelKey = segmentPixelKey(interpolated, pixelWidth);
      if (pixelKey !== drawnPixelKey) {
        // 中间帧也画分隔线，跟着分界走：每秒一次的更新里分隔线不再一隐一现。
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

  for (const segment of segments) {
    const leftPx = Math.floor(clampNumber(segment.left, 0, 100) * pixelWidth / 100);
    const rightPx = Math.floor(clampNumber(segment.right, 0, 100) * pixelWidth / 100);
    if (rightPx <= leftPx) {
      continue;
    }
    paintSegmentBody(context, segment, leftPx, rightPx, height, geometry.devicePixelRatio, presentation, paintStyle);
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
    // 45° 斜纹，从轨道左端起按固定间距排：纹路不跟着段的宽度变化滑动。
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

    const label = fitCanvasLabel((text) => paintStyle.textWidth(context, text), segment.label, availableWidth);
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

function fitCanvasLabel(
  textWidth: (text: string) => number,
  label: string,
  availableWidth: number)
{
  if (textWidth(label) <= availableWidth) {
    return label;
  }

  const suffix = "...";
  if (textWidth(suffix) > availableWidth) {
    return "";
  }

  let low = 0;
  let high = label.length;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    const candidate = `${label.slice(0, middle)}${suffix}`;
    if (textWidth(candidate) <= availableWidth) {
      low = middle;
    } else {
      high = middle - 1;
    }
  }

  return `${label.slice(0, low)}${suffix}`;
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

function paintPixelWidth(geometry: PaintGeometry) {
  return Math.max(1, Math.round(geometry.width * geometry.devicePixelRatio));
}

/** 各段左右边界落在哪个像素上（与 drawSegments 的取整一致）。颜色、标签在一段过渡内不变，不进键。 */
function segmentPixelKey(segments: ResourcePaintSegment[], pixelWidth: number) {
  let key = "";
  for (const segment of segments) {
    key += `${Math.floor(clampNumber(segment.left, 0, 100) * pixelWidth / 100)},${Math.floor(clampNumber(segment.right, 0, 100) * pixelWidth / 100)};`;
  }
  return key;
}

function paintSegmentSignature(segments: ResourcePaintSegment[]) {
  return segments
    .map((segment) => `${segment.key}:${segment.left.toFixed(4)}:${segment.right.toFixed(4)}:${segment.color}:${segment.distinctColor ?? ""}:${segment.sharedFraction ?? 0}:${segment.hatched ? 1 : 0}`)
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

// 两种配色都画分隔线：类型固定色里同组的段同色，异色区分的散列色也可能让相邻两段撞色。
function shouldDrawDividers(
  segments: ResourcePaintSegment[],
  drawDividers: boolean)
{
  return drawDividers && segments.length > 1;
}

/**
 * 一次绘制（一次数据更新，或一段 360 ms 的过渡）内不变的画布样式：字体、CSS 变量解析出的颜色、文字宽度。
 * 每次更新新建一份，过渡中的每一帧复用，不再逐帧读计算样式（逐帧读会强制样式重算）。
 */
interface CanvasPaintStyle {
  font(): string;
  color(value: string): string;
  textWidth(context: CanvasRenderingContext2D, text: string): number;
}

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
    animations: document.body.dataset.animations ?? "normal",
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
