import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";
import type { DiskUsageViewport } from "./diskUsageViewport.ts";

/**
 * 方格图的绘制规则。
 *
 * **方格只画一次，之后是贴图**：整份布局先渲染进一张位图（见 renderTreemapSheet），
 * 每一帧只把这张位图按视口 drawImage 上去。所以拖动和缩放的开销跟方格数量无关，
 * 几万个和几十万个一样快。名字和选中框是每帧现画的，这样放大之后字仍然是清晰的，
 * 而且它们数量很少。命中测试走布局数据，跟画法无关，所以每个方格照样能单独响应。
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
 *
 * 名字之间也要让位：子方格在父方格里面，两个名字会重叠，
 * 后画的把先画的盖掉一截，"Windows Kits" 就成了 "10 ndows Kits"。
 * 所以按由浅到深的顺序画，先占住位置的保留，后来的撞上了就不画 ——
 * 外层名字负责定位，里层名字放大之后自然会露出来。
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
  /** 整张图预先渲染好的那张位图。见 <see cref="renderTreemapSheet" />。 */
  sheet: DiskUsageSheet | null;
  viewport: DiskUsageViewport;
  width: number;
  height: number;
  devicePixelRatio: number;
  theme: DiskUsagePaintTheme;
  /** 选中的节点序号，没有就是 -1。 */
  selectedNodeId: number;
  /** 取节点名字。名字跟布局一起发下来了，这是纯查表。 */
  labelOf: (nodeId: number) => string;
}

/**
 * 整张图渲染好的一张位图，外加它覆盖的那块单位空间范围。
 *
 * 方格本身只画这一次。之后缩放平移都只是把这张图按视口贴上去 ——
 * 一次 drawImage，和方格有几万个还是几十万个没关系。
 */
export interface DiskUsageSheet {
  canvas: HTMLCanvasElement;
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
  /**
   * 这张位图是按"放大多少倍"渲染的。
   *
   * 1 表示和这块范围铺满画布时 1:1。放大之后位图会被拉大，就糊了，
   * 所以放大到一定程度要按更高的倍数重渲一张。见 magnificationOf。
   */
  magnification: number;
}

/**
 * 当前这块可见范围相对于位图覆盖范围放大了多少倍。
 *
 * 位图覆盖的是布局那块范围；视口只看其中的一小块时，那一小块被放大到整个画布，
 * 放大的倍数就是两者跨度之比。位图得按这个倍数渲染才不会糊。
 */
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

/** 位图最多这么多像素。再大的话显存和渲染时间都不划算。 */
const maximumSheetPixels = 8_000_000;

/**
 * 把这份布局的所有方格画进一张位图。
 *
 * 只在布局或配色变了的时候做一次。它是整个页面最重的一步，
 * 但它不在交互路径上 —— 拖动和缩放碰不到它。
 */
export function renderTreemapSheet(
  layout: DiskUsageLayout,
  theme: DiskUsagePaintTheme,
  canvasWidth: number,
  canvasHeight: number,
  magnification = 1
): DiskUsageSheet | null {
  // 按当前放大倍数提高渲染精度：视口只看其中一小块时，那一小块要顶满整个画布，
  // 位图就得按相应倍数渲染，否则放大之后看到的是被拉大的糊图。
  const requestedWidth = canvasWidth * magnification;
  const requestedHeight = canvasHeight * magnification;
  const span = {
    x: Math.max(1e-9, layout.view.maxX - layout.view.minX),
    y: Math.max(1e-9, layout.view.maxY - layout.view.minY)
  };
  const wanted = Math.max(1, Math.round(requestedWidth))
    * Math.max(1, Math.round(requestedHeight));
  // 超出像素预算就整体缩一档，长宽比保持不变。
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
    // 根方格只是背景，画了会把所有子方格盖住。
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

    // 内描边：画在里侧，够大才画。这就是"类边框纹理"，不占外部像素。
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
    // 像素预算截断过的话，实际精度就没到请求的倍数，如实记下来。
    magnification: magnification * shrink
  };
}

/** 小于这个边长的方格不画内描边：描边会把它整个糊住。 */
const innerStrokeMinimumSide = 14;

/** 名字至少要这么宽、这么高才画。 */
const labelMinimumWidth = 44;
const labelMinimumHeight = 16;

/** 名字底衬的高度，占位和绘制用的是同一个值。 */
const labelChipHeight = 15;

/**
 * 记名字占了画布上哪些地方，用来判断后来的名字会不会压到先画的。
 *
 * 用固定粒度的格子而不是两两比大小：可见的名字最多上千个，
 * 两两比就是百万次，拖动时会卡；格子是按面积算的，一个名字只碰几个格。
 * 粒度偏粗会让紧挨着的两个名字也算撞上，这个方向是安全的 —— 宁可不画。
 */
export class LabelOccupancy {
  private readonly columns: number;
  private readonly taken: Uint8Array;
  private readonly cell: number;

  constructor(width: number, height: number, cell = 8) {
    this.cell = cell;
    this.columns = Math.max(1, Math.ceil(width / cell));
    this.taken = new Uint8Array(this.columns * Math.max(1, Math.ceil(height / cell)));
  }

  /** 这块地方还空着就占下并返回 true，已经被占了就返回 false。 */
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

  // 方格是整张贴上去的，不是一个个画的。
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

  // 名字每帧现画：烘进位图的话一放大就跟着糊掉，而且字会被拉变形。
  // 它数量很少（放得下才画，而且互相让位），现画不费事。
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

    // 量一次：放不下整个名字就不画，不做省略号 —— 半个文件名没有意义。
    const measured = context.measureText(label);
    if (measured.width > boxWidth - 8) {
      continue;
    }

    // 先占位置：这块地方已经有名字了就整个不画，不画半截压上去的字。
    const chipLeft = left + 3;
    const chipTop = top + 3;
    const chipWidth = measured.width + 5;
    if (!occupancy.tryReserve(chipLeft, chipTop, chipWidth, labelChipHeight)) {
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
