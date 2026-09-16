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
 *
 * 方向按"鼠标在右，视口就往右走"来 —— 也就是往右贴边会看到右边的内容，
 * 图看上去朝左滑。先前是反的（贴右边反而往左看），多数人不习惯。
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

/**
 * 允许把图推出画布，但不许推到找不回来。
 *
 * 先前是贴着边界夹死的：手一松就弹回去，看着像"自动归位"，
 * 而且缩放为 1 时根本没法移动。现在两个方向各留一整屏的余量，
 * 也就是最多能把图整个推出画布外，再多就不行了 ——
 * 有余量才能把角落里的方格拖到中间看，有上限才不会推到一片空白里回不来。
 * 真推出去了也有"复位"可以一键回到整图。
 */
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

/**
 * 当前看得见的那块单位空间矩形。
 *
 * 缩放 S 时画布正好覆盖 1/S 见方的一块，左上角就是平移量。
 * 后端按这块矩形剪枝：在它外面的方格整棵跳过。
 */
export function visibleUnitRect(viewport: DiskUsageViewport): {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
} {
  const visible = 1 / viewport.scale;
  // 视图可以被推出图外，所以这里要和 [0,1] 取交集：
  // 图外面没有方格，报出去只会让后端算一块空的。
  return {
    minX: clamp(viewport.offsetX, 0, 1),
    minY: clamp(viewport.offsetY, 0, 1),
    maxX: clamp(viewport.offsetX + visible, 0, 1),
    maxY: clamp(viewport.offsetY + visible, 0, 1)
  };
}

/**
 * 客户端此刻看到的东西，发给后端决定布局发哪些方格。
 *
 * 三样东西共同决定一个方格在屏幕上有多大：画布的物理像素尺寸
 * （窗口缩放和屏幕缩放都算在里面）、滚轮倍数、以及看得见的那块范围。
 */
export interface DiskUsageViewWindow {
  /** 画布的**物理**像素尺寸，也就是 CSS 尺寸乘以设备像素比。 */
  pixelWidth: number;
  pixelHeight: number;
  scale: number;
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

/** 放大到多少倍才值得换一份更细的布局。 */
const refineScaleRatio = 1.45;

/**
 * 新视图比手上这份布局多看得见多少细节，值不值得再要一份。
 *
 * 两种情况要换：画面放大了（更多方格够得上像素门槛），
 * 或者视野移到了原来那块范围之外（那边的方格当时根本没发下来）。
 * 缩小和原地不动都不用换 —— 手上这份已经覆盖了，再要一次只是白跑一趟。
 */
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
