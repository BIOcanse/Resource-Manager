/** Pure viewport mapping from fixed unit-space layout to canvas pixels; pan and zoom require no backend call or layout recomputation. */
export interface DiskUsageViewport {
  /** Zoom scale; one fits the full map to the canvas. */
  scale: number;
  /** Translation in unit-space coordinates. */
  offsetX: number;
  offsetY: number;
}

export const identityViewport: DiskUsageViewport = {
  scale: 1,
  offsetX: 0,
  offsetY: 0
};

const minimumScale = 1;
const maximumScale = 4096;

/** Zoom around a canvas anchor while preserving the content beneath it. */
export function zoomAt(
  viewport: DiskUsageViewport,
  canvasWidth: number,
  canvasHeight: number,
  pointerX: number,
  pointerY: number,
  factor: number
): DiskUsageViewport {
  const nextScale = clamp(viewport.scale * factor, minimumScale, maximumScale);
  if (nextScale === viewport.scale) {
    return viewport;
  }

  // Keep the anchor's unit coordinate at the same canvas pixel after zoom.
  const unitX = viewport.offsetX + pointerX / (canvasWidth * viewport.scale);
  const unitY = viewport.offsetY + pointerY / (canvasHeight * viewport.scale);
  return clampToBounds({
    scale: nextScale,
    offsetX: unitX - pointerX / (canvasWidth * nextScale),
    offsetY: unitY - pointerY / (canvasHeight * nextScale)
  });
}

/** Pan by canvas pixels. */
export function panByPixels(
  viewport: DiskUsageViewport,
  canvasWidth: number,
  canvasHeight: number,
  deltaX: number,
  deltaY: number
): DiskUsageViewport {
  return clampToBounds({
    scale: viewport.scale,
    offsetX: viewport.offsetX + deltaX / (canvasWidth * viewport.scale),
    offsetY: viewport.offsetY + deltaY / (canvasHeight * viewport.scale)
  });
}

/** Return edge-pan displacement toward the pointer's edge, or zero outside the edge region. */
export function edgePanDelta(
  pointerX: number,
  pointerY: number,
  canvasWidth: number,
  canvasHeight: number,
  edgeThickness = 48,
  pixelsPerSecond = 900,
  elapsedSeconds = 1 / 60
): { deltaX: number; deltaY: number } {
  const step = pixelsPerSecond * elapsedSeconds;
  let deltaX = 0;
  let deltaY = 0;
  if (pointerX >= 0 && pointerX < edgeThickness) {
    deltaX = -step * (1 - pointerX / edgeThickness);
  } else if (pointerX <= canvasWidth && pointerX > canvasWidth - edgeThickness) {
    deltaX = step * (1 - (canvasWidth - pointerX) / edgeThickness);
  }
  if (pointerY >= 0 && pointerY < edgeThickness) {
    deltaY = -step * (1 - pointerY / edgeThickness);
  } else if (pointerY <= canvasHeight && pointerY > canvasHeight - edgeThickness) {
    deltaY = step * (1 - (canvasHeight - pointerY) / edgeThickness);
  }
  return { deltaX, deltaY };
}

/** Allow bounded off-canvas panning so corner tiles can be centered and reset remains useful. */
const overscrollSpans = 1;

export function clampToBounds(viewport: DiskUsageViewport): DiskUsageViewport {
  const visible = 1 / viewport.scale;
  const slack = visible * overscrollSpans;
  const low = -slack;
  const high = Math.max(0, 1 - visible) + slack;
  return {
    scale: viewport.scale,
    offsetX: clamp(viewport.offsetX, low, high),
    offsetY: clamp(viewport.offsetY, low, high)
  };
}

/** Convert unit coordinates to canvas pixels. */
export function unitToPixels(
  viewport: DiskUsageViewport,
  canvasWidth: number,
  canvasHeight: number,
  unitX: number,
  unitY: number
): { x: number; y: number } {
  return {
    x: (unitX - viewport.offsetX) * canvasWidth * viewport.scale,
    y: (unitY - viewport.offsetY) * canvasHeight * viewport.scale
  };
}

/** Convert canvas pixels to unit coordinates. */
export function pixelsToUnit(
  viewport: DiskUsageViewport,
  canvasWidth: number,
  canvasHeight: number,
  pixelX: number,
  pixelY: number
): { x: number; y: number } {
  return {
    x: viewport.offsetX + pixelX / (canvasWidth * viewport.scale),
    y: viewport.offsetY + pixelY / (canvasHeight * viewport.scale)
  };
}

/** Visible unit-space bounds used for local layout pruning. */
export function visibleUnitRect(viewport: DiskUsageViewport): {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
} {
  const visible = 1 / viewport.scale;
  // Intersect with unit space because the viewport may extend outside the map.
  // Off-map regions contain no tiles to refine.
  return {
    minX: clamp(viewport.offsetX, 0, 1),
    minY: clamp(viewport.offsetY, 0, 1),
    maxX: clamp(viewport.offsetX + visible, 0, 1),
    maxY: clamp(viewport.offsetY + visible, 0, 1)
  };
}

/** Physical canvas size, zoom, and visible bounds determine local layout detail. */
export interface DiskUsageViewWindow {
  /** Canvas CSS dimensions multiplied by device pixel ratio. */
  pixelWidth: number;
  pixelHeight: number;
  scale: number;
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

/** Zoom increase required before requesting finer detail. */
const refineScaleRatio = 1.45;

/** Refine when zoom or bounds reveal uncovered detail; unchanged or covered views reuse the layout. */
export function needsMoreDetail(
  shown: DiskUsageViewWindow,
  next: DiskUsageViewWindow
): boolean {
  const shownDetail = shown.scale * Math.min(shown.pixelWidth, shown.pixelHeight);
  const nextDetail = next.scale * Math.min(next.pixelWidth, next.pixelHeight);
  if (nextDetail >= shownDetail * refineScaleRatio) {
    return true;
  }
  return next.minX < shown.minX
    || next.minY < shown.minY
    || next.maxX > shown.maxX
    || next.maxY > shown.maxY;
}

function clamp(value: number, low: number, high: number) {
  return value < low ? low : value > high ? high : value;
}
