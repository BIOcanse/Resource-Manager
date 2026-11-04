import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";
import type { DiskUsageViewport } from "./diskUsageViewport.ts";

/** Cache tile geometry in a bitmap so pan and zoom avoid per-tile painting. Draw crisp labels and selection separately; reserve label space from shallow to deep and keep strokes inside tiles. */
export interface DiskUsagePaintTheme {
  /** Base color for directory tiles. */
  directoryHue: number;
  /** Base color for file tiles. */
  fileHue: number;
  surface: string;
  label: string;
  labelShadow: string;
  selection: string;
}

export interface DiskUsagePaintOptions {
  layout: DiskUsageLayout;
  /** Prerendered bitmap produced by renderTreemapSheet. */
  sheet: DiskUsageSheet | null;
  viewport: DiskUsageViewport;
  width: number;
  height: number;
  devicePixelRatio: number;
  theme: DiskUsagePaintTheme;
  /** Selected node identity, or -1 when unselected. */
  selectedNodeId: number;
  /** Read names directly from layout data. */
  labelOf: (nodeId: number) => string;
}

/** Cached bitmap and its unit-space bounds, reused during viewport changes. */
export interface DiskUsageSheet {
  canvas: HTMLCanvasElement;
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
  /** Bitmap render magnification; higher zoom may require rerendering for sharpness. */
  magnification: number;
}

/** Visible-to-bitmap span ratio determines the render magnification needed for sharp pixels. */
export function magnificationOf(
  layout: DiskUsageLayout,
  viewportScale: number
): number {
  const layoutSpan = Math.max(
    1e-9,
    Math.max(layout.view.maxX - layout.view.minX, layout.view.maxY - layout.view.minY));
  const visibleSpan = 1 / Math.max(1e-9, viewportScale);
  return Math.max(1, layoutSpan / visibleSpan);
}

/** Pixel budget bounds bitmap memory and render cost. */
const maximumSheetPixels = 8_000_000;

/** Render tiles when layout, theme, or required detail changes; viewport movement reuses the bitmap. */
export function renderTreemapSheet(
  layout: DiskUsageLayout,
  theme: DiskUsagePaintTheme,
  canvasWidth: number,
  canvasHeight: number,
  magnification = 1
): DiskUsageSheet | null {
  // Increase bitmap resolution to match the current magnification.
  // Visible bitmap regions must retain enough pixels to fill the canvas.
  const requestedWidth = canvasWidth * magnification;
  const requestedHeight = canvasHeight * magnification;
  const span = {
    x: Math.max(1e-9, layout.view.maxX - layout.view.minX),
    y: Math.max(1e-9, layout.view.maxY - layout.view.minY)
  };
  const wanted = Math.max(1, Math.round(requestedWidth))
    * Math.max(1, Math.round(requestedHeight));
  // Shrink uniformly to meet the pixel budget without changing aspect ratio.
  const shrink = wanted > maximumSheetPixels
    ? Math.sqrt(maximumSheetPixels / wanted)
    : 1;
  const sheetWidth = Math.max(1, Math.round(requestedWidth * shrink));
  const sheetHeight = Math.max(1, Math.round(requestedHeight * shrink));

  const canvas = document.createElement("canvas");
  canvas.width = sheetWidth;
  canvas.height = sheetHeight;
  const context = canvas.getContext("2d");
  if (!context) {
    return null;
  }

  context.fillStyle = theme.surface;
  context.fillRect(0, 0, sheetWidth, sheetHeight);

  const count = layout.nodeIds.length;
  for (let index = 0; index < count; index++) {
    // The root is background; painting it would cover descendants.
    if (layout.depths[index] === 0) {
      continue;
    }
    const left = (layout.x[index] - layout.view.minX) / span.x * sheetWidth;
    const top = (layout.y[index] - layout.view.minY) / span.y * sheetHeight;
    const boxWidth = layout.width[index] / span.x * sheetWidth;
    const boxHeight = layout.height[index] / span.y * sheetHeight;
    if (boxWidth < 0.05 || boxHeight < 0.05) {
      continue;
    }

    const isDirectory = layout.directoryFlags[index] === 1;
    context.fillStyle = tileColor(theme, layout.depths[index], index, isDirectory);
    context.fillRect(left, top, boxWidth, boxHeight);

    // Draw inset strokes only when tiles are large enough.
    if (boxWidth >= innerStrokeMinimumSide && boxHeight >= innerStrokeMinimumSide) {
      context.strokeStyle = innerStrokeColor(layout.depths[index], isDirectory);
      context.lineWidth = 1;
      context.strokeRect(left + 0.5, top + 0.5, boxWidth - 1, boxHeight - 1);
    }
  }

  return {
    canvas,
    minX: layout.view.minX,
    minY: layout.view.minY,
    maxX: layout.view.maxX,
    maxY: layout.view.maxY,
    // Record actual magnification after applying the pixel budget.
    magnification: magnification * shrink
  };
}

/** Smaller tiles omit strokes so their contents remain visible. */
const innerStrokeMinimumSide = 14;

/** Minimum dimensions for drawing a full label. */
const labelMinimumWidth = 44;
const labelMinimumHeight = 16;

/** Shared label backing height for occupancy and painting. */
const labelChipHeight = 15;

/** A coarse occupancy grid avoids pairwise label comparisons; conservative collisions hide labels rather than overlap them. */
export class LabelOccupancy {
  private readonly columns: number;
  private readonly taken: Uint8Array;
  private readonly cell: number;

  constructor(width: number, height: number, cell = 8) {
    this.cell = cell;
    this.columns = Math.max(1, Math.ceil(width / cell));
    this.taken = new Uint8Array(this.columns * Math.max(1, Math.ceil(height / cell)));
  }

  /** Reserve an unoccupied region and return true; return false on collision. */
  tryReserve(left: number, top: number, width: number, height: number): boolean {
    const firstColumn = Math.max(0, Math.floor(left / this.cell));
    const lastColumn = Math.min(this.columns - 1, Math.floor((left + width) / this.cell));
    const rows = this.taken.length / this.columns;
    const firstRow = Math.max(0, Math.floor(top / this.cell));
    const lastRow = Math.min(rows - 1, Math.floor((top + height) / this.cell));

    for (let row = firstRow; row <= lastRow; row++) {
      for (let column = firstColumn; column <= lastColumn; column++) {
        if (this.taken[row * this.columns + column] === 1) {
          return false;
        }
      }
    }
    for (let row = firstRow; row <= lastRow; row++) {
      for (let column = firstColumn; column <= lastColumn; column++) {
        this.taken[row * this.columns + column] = 1;
      }
    }
    return true;
  }
}

export function paintTreemap(
  context: CanvasRenderingContext2D,
  options: DiskUsagePaintOptions
): void {
  const { layout, viewport, width, height, theme } = options;
  context.save();
  context.scale(options.devicePixelRatio, options.devicePixelRatio);
  context.clearRect(0, 0, width, height);
  context.fillStyle = theme.surface;
  context.fillRect(0, 0, width, height);

  const count = layout.nodeIds.length;
  const scaleX = width * viewport.scale;
  const scaleY = height * viewport.scale;

  // Paint the cached tile bitmap in one operation.
  if (options.sheet) {
    const sheet = options.sheet;
    const left = (sheet.minX - viewport.offsetX) * scaleX;
    const top = (sheet.minY - viewport.offsetY) * scaleY;
    const right = (sheet.maxX - viewport.offsetX) * scaleX;
    const bottom = (sheet.maxY - viewport.offsetY) * scaleY;
    if (right > left && bottom > top) {
      context.drawImage(sheet.canvas, left, top, right - left, bottom - top);
    }
  }

  // Draw labels per frame so zoom cannot blur or distort their text.
  // Size and overlap filtering keeps the visible label set small.
  const labelCandidates: number[] = [];
  for (let index = 0; index < count; index++) {
    if (layout.depths[index] === 0) {
      continue;
    }
    const boxWidth = layout.width[index] * scaleX;
    if (boxWidth < labelMinimumWidth) {
      continue;
    }
    const boxHeight = layout.height[index] * scaleY;
    if (boxHeight < labelMinimumHeight) {
      continue;
    }
    const left = (layout.x[index] - viewport.offsetX) * scaleX;
    const top = (layout.y[index] - viewport.offsetY) * scaleY;
    if (left + boxWidth < 0 || top + boxHeight < 0 || left > width || top > height) {
      continue;
    }
    labelCandidates.push(index);
  }

  paintLabels(context, options, labelCandidates, scaleX, scaleY);
  paintSelection(context, options, scaleX, scaleY);
  context.restore();
}

function paintLabels(
  context: CanvasRenderingContext2D,
  options: DiskUsagePaintOptions,
  candidates: number[],
  scaleX: number,
  scaleY: number
) {
  const { layout, viewport, theme } = options;
  context.textBaseline = "top";
  context.font = "12px 'Segoe UI Variable Text', 'Segoe UI', sans-serif";
  const occupancy = new LabelOccupancy(options.width, options.height);

  for (const index of candidates) {
    const label = options.labelOf(layout.nodeIds[index]);
    if (!label) {
      continue;
    }
    const left = (layout.x[index] - viewport.offsetX) * scaleX;
    const top = (layout.y[index] - viewport.offsetY) * scaleY;
    const boxWidth = layout.width[index] * scaleX;

    // Measure once and omit labels that cannot fit in full.
    const measured = context.measureText(label);
    if (measured.width > boxWidth - 8) {
      continue;
    }

    // Reserve the complete label area before drawing to prevent partial overlaps.
    const chipLeft = left + 3;
    const chipTop = top + 3;
    const chipWidth = measured.width + 5;
    if (!occupancy.tryReserve(chipLeft, chipTop, chipWidth, labelChipHeight)) {
      continue;
    }

    // Clip text to its own tile.
    // Adjacent labels must not join visually across tile boundaries.
    const boxHeight = layout.height[index] * scaleY;
    context.save();
    context.beginPath();
    context.rect(left, top, boxWidth, boxHeight);
    context.clip();
    // A backing surface keeps labels legible across tile shades.
    context.fillStyle = theme.labelShadow;
    context.fillRect(chipLeft, chipTop, chipWidth, labelChipHeight);
    context.fillStyle = theme.label;
    context.fillText(label, left + 5, top + 5);
    context.restore();
  }
}

function paintSelection(
  context: CanvasRenderingContext2D,
  options: DiskUsagePaintOptions,
  scaleX: number,
  scaleY: number
) {
  if (options.selectedNodeId < 0) {
    return;
  }
  const { layout, viewport, theme } = options;
  for (let index = 0; index < layout.nodeIds.length; index++) {
    if (layout.nodeIds[index] !== options.selectedNodeId) {
      continue;
    }
    const left = (layout.x[index] - viewport.offsetX) * scaleX;
    const top = (layout.y[index] - viewport.offsetY) * scaleY;
    const boxWidth = layout.width[index] * scaleX;
    const boxHeight = layout.height[index] * scaleY;
    // Keep the selection stroke inside even the smallest tile.
    context.strokeStyle = theme.selection;
    context.lineWidth = 2;
    context.strokeRect(
      left + 1,
      top + 1,
      Math.max(1, boxWidth - 2),
      Math.max(1, boxHeight - 2));
    return;
  }
}

/** Vary sibling brightness and darken by depth to reveal adjacency and hierarchy without borders. */
function tileColor(
  theme: DiskUsagePaintTheme,
  depth: number,
  index: number,
  isDirectory: boolean
) {
  const hue = isDirectory ? theme.directoryHue : theme.fileHue;
  const depthShade = Math.min(depth, 6);
  // Index-based brightness offsets remain stable without extra state.
  const ripple = (index % 5) * 2.2;
  const lightness = isDirectory
    ? 34 - depthShade * 2.4 + ripple
    : 58 - depthShade * 2.8 + ripple;
  const saturation = isDirectory ? 26 : 46;
  return `hsl(${hue} ${saturation}% ${lightness}%)`;
}

function innerStrokeColor(depth: number, isDirectory: boolean) {
  const alpha = isDirectory ? 0.34 : 0.22;
  return `hsl(0 0% ${depth % 2 === 0 ? 100 : 0}% / ${alpha})`;
}

/** Hit-test back to front to match paint order. */
export function hitTest(
  layout: DiskUsageLayout,
  viewport: DiskUsageViewport,
  width: number,
  height: number,
  pixelX: number,
  pixelY: number
): number {
  const scaleX = width * viewport.scale;
  const scaleY = height * viewport.scale;
  for (let index = layout.nodeIds.length - 1; index >= 0; index--) {
    if (layout.depths[index] === 0) {
      continue;
    }
    const left = (layout.x[index] - viewport.offsetX) * scaleX;
    const top = (layout.y[index] - viewport.offsetY) * scaleY;
    if (pixelX < left || pixelY < top) {
      continue;
    }
    if (pixelX > left + layout.width[index] * scaleX) {
      continue;
    }
    if (pixelY > top + layout.height[index] * scaleY) {
      continue;
    }
    return layout.nodeIds[index];
  }
  return -1;
}

/** Return a tile's visible center for keyboard menus, the nearest canvas edge when offscreen, or null when absent. */
export function visibleNodeCentre(
  layout: DiskUsageLayout,
  viewport: DiskUsageViewport,
  width: number,
  height: number,
  nodeId: number
): { x: number; y: number } | null {
  const index = layout.indexByNodeId.get(nodeId);
  if (index === undefined) {
    return null;
  }
  const scaleX = width * viewport.scale;
  const scaleY = height * viewport.scale;
  const left = (layout.x[index] - viewport.offsetX) * scaleX;
  const top = (layout.y[index] - viewport.offsetY) * scaleY;
  const right = left + layout.width[index] * scaleX;
  const bottom = top + layout.height[index] * scaleY;
  const clamp = (value: number, limit: number) => Math.min(limit, Math.max(0, value));
  return {
    x: clamp((Math.max(left, 0) + Math.min(right, width)) / 2, width),
    y: clamp((Math.max(top, 0) + Math.min(bottom, height)) / 2, height)
  };
}
