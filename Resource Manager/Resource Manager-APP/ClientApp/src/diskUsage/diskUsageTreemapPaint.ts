import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";
import type { DiskUsageViewport } from "./diskUsageViewport.ts";

/**
 * 方格图的绘制规则。
 *
 * **方格不画边框**：小文件本来就只有几个像素，一圈边框会把它整个吃掉。
 * 区分靠三样东西，全部画在矩形内部：
 *
 * 1. 兄弟节点之间用**明度阶梯**：同色相，按大小顺序依次微调亮度。
 * 2. 目录层级用**内缩的内描边**：描边画在矩形里侧，不占外部一个像素，
 *    而且只有矩形够大（两边都超过阈值）时才画，小格子干脆不画。
 * 3. 文件和目录用**不同色相**，一眼分得开谁是容器谁是内容。
 *
 * 名字只在矩形装得下时画（宽够放字、高够放行），装不下就不画 ——
 * 画一半的字比不画更糟。
 */
export interface DiskUsagePaintTheme {
  /** 目录方格的基色。 */
  directoryHue: number;
  /** 文件方格的基色。 */
  fileHue: number;
  surface: string;
  label: string;
  labelShadow: string;
  selection: string;
}

export interface DiskUsagePaintOptions {
  layout: DiskUsageLayout;
  viewport: DiskUsageViewport;
  width: number;
  height: number;
  devicePixelRatio: number;
  theme: DiskUsagePaintTheme;
  /** 选中的节点序号，没有就是 -1。 */
  selectedNodeId: number;
  /** 取节点名字，拿不到就返回空串（名字是按需取的，不随布局一起发）。 */
  labelOf: (nodeId: number) => string;
}

/** 小于这个边长的方格不画内描边：描边会把它整个糊住。 */
const innerStrokeMinimumSide = 14;

/** 名字至少要这么宽、这么高才画。 */
const labelMinimumWidth = 44;
const labelMinimumHeight = 16;

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
  const labelCandidates: number[] = [];

  for (let index = 0; index < count; index++) {
    const left = (layout.x[index] - viewport.offsetX) * scaleX;
    const top = (layout.y[index] - viewport.offsetY) * scaleY;
    const boxWidth = layout.width[index] * scaleX;
    const boxHeight = layout.height[index] * scaleY;

    // 视口外的直接跳过，缩放到很深时这一步省掉绝大部分绘制。
    if (left + boxWidth < 0 || top + boxHeight < 0 || left > width || top > height) {
      continue;
    }
    // 根节点只是背景，不画它自己，否则会把所有子节点盖住。
    if (layout.depths[index] === 0) {
      continue;
    }
    if (boxWidth < 0.5 || boxHeight < 0.5) {
      continue;
    }

    const isDirectory = layout.directoryFlags[index] === 1;
    context.fillStyle = tileColor(theme, layout.depths[index], index, isDirectory);
    context.fillRect(left, top, boxWidth, boxHeight);

    // 内描边：画在里侧，够大才画。这就是"类边框纹理"，不占外部像素。
    if (boxWidth >= innerStrokeMinimumSide && boxHeight >= innerStrokeMinimumSide) {
      context.strokeStyle = innerStrokeColor(layout.depths[index], isDirectory);
      context.lineWidth = 1;
      context.strokeRect(left + 0.5, top + 0.5, boxWidth - 1, boxHeight - 1);
    }

    if (boxWidth >= labelMinimumWidth && boxHeight >= labelMinimumHeight) {
      labelCandidates.push(index);
    }
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

  for (const index of candidates) {
    const label = options.labelOf(layout.nodeIds[index]);
    if (!label) {
      continue;
    }
    const left = (layout.x[index] - viewport.offsetX) * scaleX;
    const top = (layout.y[index] - viewport.offsetY) * scaleY;
    const boxWidth = layout.width[index] * scaleX;

    // 量一次：放不下整个名字就不画，不做省略号 —— 半个文件名没有意义。
    const measured = context.measureText(label);
    if (measured.width > boxWidth - 8) {
      continue;
    }

    // 裁到自己的格子里再画。相邻两个窄格子各画各的名字时，
    // 不裁的话两串字会紧挨着连成一个词（"adapters" + "topology"）。
    const boxHeight = layout.height[index] * scaleY;
    context.save();
    context.beginPath();
    context.rect(left, top, boxWidth, boxHeight);
    context.clip();
    // 底衬让名字在深浅不一的方格上都读得清，比描边字干净。
    context.fillStyle = theme.labelShadow;
    context.fillRect(left + 3, top + 3, measured.width + 5, 15);
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
    // 选中框同样画在里侧：选中一个 3 像素的小文件也不会挤到邻居。
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

/**
 * 方格颜色。同一深度的兄弟按顺序做明度阶梯，深一层整体压暗一点，
 * 这样层级和相邻关系都看得出来，而且完全不需要边框。
 */
function tileColor(
  theme: DiskUsagePaintTheme,
  depth: number,
  index: number,
  isDirectory: boolean
) {
  const hue = isDirectory ? theme.directoryHue : theme.fileHue;
  const depthShade = Math.min(depth, 6);
  // 相邻方格的明度错开，靠序号取模，稳定且不需要额外状态。
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

/** 画布上的一点落在哪个方格里。后画的在上面，所以从后往前找。 */
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
