/**
 * 方格图的视口。
 *
 * 布局是单位空间 [0,1]×[0,1] 里的一组矩形，对同一个根是固定的。
 * 视口负责把单位空间映射到画布像素：一个缩放倍数加一个平移量，没有别的状态。
 * 所以缩放和平移都是纯计算，不回后端，也不重算布局。
 */
export interface DiskUsageViewport {
  /** 缩放倍数。1 表示整张图刚好铺满画布。 */
  scale: number;
  /** 平移量，单位空间坐标。 */
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

/**
 * 以画布上的某个点为锚缩放。锚点在图上对应的位置保持不动 ——
 * 滚轮缩放要的就是"指哪儿放大哪儿"。
 */
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

  // 锚点当前对应的单位空间坐标，缩放后仍要落在同一个像素上。
  const unitX = viewport.offsetX + pointerX / (canvasWidth * viewport.scale);
  const unitY = viewport.offsetY + pointerY / (canvasHeight * viewport.scale);
  return clampToBounds({
    scale: nextScale,
    offsetX: unitX - pointerX / (canvasWidth * nextScale),
    offsetY: unitY - pointerY / (canvasHeight * nextScale)
  });
}

/** 按画布像素平移。 */
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

/**
 * 鼠标贴着边界时往那个方向移动视图。
 * 返回这一帧应该平移的像素量；不在边界区域内就是 0，不动。
 */
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
    deltaX = step * (1 - pointerX / edgeThickness);
  } else if (pointerX <= canvasWidth && pointerX > canvasWidth - edgeThickness) {
    deltaX = -step * (1 - (canvasWidth - pointerX) / edgeThickness);
  }
  if (pointerY >= 0 && pointerY < edgeThickness) {
    deltaY = step * (1 - pointerY / edgeThickness);
  } else if (pointerY <= canvasHeight && pointerY > canvasHeight - edgeThickness) {
    deltaY = -step * (1 - (canvasHeight - pointerY) / edgeThickness);
  }
  return { deltaX, deltaY };
}

/** 视口不允许划出图外：缩放为 1 时整张图铺满，更大时边界贴边。 */
export function clampToBounds(viewport: DiskUsageViewport): DiskUsageViewport {
  const visible = 1 / viewport.scale;
  const maximumOffset = Math.max(0, 1 - visible);
  return {
    scale: viewport.scale,
    offsetX: clamp(viewport.offsetX, 0, maximumOffset),
    offsetY: clamp(viewport.offsetY, 0, maximumOffset)
  };
}

/** 单位空间坐标 → 画布像素。 */
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

/** 画布像素 → 单位空间坐标。 */
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

function clamp(value: number, low: number, high: number) {
  return value < low ? low : value > high ? high : value;
}
