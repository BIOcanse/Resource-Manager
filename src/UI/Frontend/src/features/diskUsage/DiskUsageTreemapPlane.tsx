import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { readDocumentTheme } from "../../presentation/documentTheme";
import {
  hitTest,
  magnificationOf,
  paintTreemap,
  renderTreemapSheet,
  type DiskUsagePaintOptions,
  type DiskUsagePaintTheme,
  type DiskUsageSheet
} from "./diskUsageTreemapPaint.ts";
import {
  clampToBounds,
  edgePanDelta,
  identityViewport,
  panByPixels,
  visibleUnitRect,
  zoomAt,
  type DiskUsageViewport,
  type DiskUsageViewWindow
} from "./diskUsageViewport.ts";
import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";

/**
 * 方格图的画布。
 *
 * 布局是固定的单位空间矩形，这里只负责把它映射到像素并画出来。
 * 缩放、平移、选中都只改视口或选中项，不回后端。
 */
export function DiskUsageTreemapPlane(props: {
  layout: DiskUsageLayout;
  selectedNodeId: number;
  labelOf: (nodeId: number) => string;
  onSelect: (nodeId: number) => void;
  onActivate: (nodeId: number) => void;
  onContextMenu: (nodeId: number, clientX: number, clientY: number) => void;
  onHover: (nodeId: number, clientX: number, clientY: number) => void;
  /**
   * 视图稳定下来之后报一次当前看到的范围和物理像素尺寸。
   * 上层据此决定要不要换一份更细的布局 —— 放大之后原先太小的方格就该出现了。
   */
  onViewChanged: (view: DiskUsageViewWindow) => void;
  /** 这个数一变就把视口复位成整图。上层的「复位」按钮靠它驱动。 */
  resetNonce: number;
}) {
  let canvas: HTMLCanvasElement | undefined;
  let container: HTMLDivElement | undefined;
  const [viewport, setViewport] = createSignal<DiskUsageViewport>(identityViewport);
  const [size, setSize] = createSignal({ width: 0, height: 0 });
  const [theme, setTheme] = createSignal(readPaintTheme());

  // 鼠标贴边时持续平移。指针离开或没贴边就停，不空转。
  let edgeTimer = 0;
  let pointerX = -1;
  let pointerY = -1;

  onMount(() => {
    const observer = new ResizeObserver(() => measure());
    if (container) {
      observer.observe(container);
    }
    measure();

    const themeObserver = new MutationObserver(() => setTheme(readPaintTheme()));
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["data-theme"]
    });

    // 窗口失焦时指针不会再有消息传过来，贴边平移必须就地停住，
    // 否则切回来会发现视图自己跑掉了。
    const release = () => {
      pointerX = -1;
      pointerY = -1;
      stopEdgePan();
      props.onHover(-1, 0, 0);
    };
    window.addEventListener("blur", release);

    onCleanup(() => {
      observer.disconnect();
      themeObserver.disconnect();
      window.removeEventListener("blur", release);
      stopEdgePan();
    });
  });

  // 换了一棵树才回到整图视角，否则会停在上一棵树的某个角落。
  //
  // 只看根节点，不看布局对象本身：放大之后会换上一份更细的布局，
  // 那时根没变，视口必须原地不动，否则一放大就被弹回整图。
  let shownRootNodeId = -1;
  createEffect(() => {
    const rootNodeId = props.layout.rootNodeId;
    if (rootNodeId === shownRootNodeId) {
      return;
    }
    shownRootNodeId = rootNodeId;
    setViewport(identityViewport);
  });

  // 「复位」：回到整图。推出画布之后靠它找回来。
  let appliedResetNonce = props.resetNonce;
  createEffect(() => {
    const nonce = props.resetNonce;
    if (nonce === appliedResetNonce) {
      return;
    }
    appliedResetNonce = nonce;
    // 正在贴边平移的话要先停住，否则下一个 16 毫秒就把刚复位的视图又推出去了。
    // 指针要是还压在边缘上，下一次 mousemove 会重新开始，这是对的。
    pointerX = -1;
    pointerY = -1;
    stopEdgePan();
    setViewport(identityViewport);
  });

  // 一次要画几万个方格，比一帧还久。所以这里只登记"该重画了"，
  // 真正画在下一个动画帧，一帧最多画一次；中间那些视口值直接跳过。
  // 贴边平移每 16 毫秒改一次视口，不这样做就会排出画不完的队。
  let paintHandle = 0;
  // 待画的那一份。晚来的直接覆盖它，所以画的永远是最新状态，
  // 而不是排队里某个已经过时的视口。
  let pending: DiskUsagePaintOptions | null = null;

  // 整张图的位图。方格只在这里画，拖动和缩放都碰不到它。
  const [sheet, setSheet] = createSignal<DiskUsageSheet | null>(null);
  // 放大到这个倍数之前，现有位图还够清楚，不用重渲。
  const [sheetMagnification, setSheetMagnification] = createSignal(1);

  createEffect(() => {
    const layout = props.layout;
    const currentTheme = theme();
    const magnification = sheetMagnification();
    const { width, height } = size();
    if (width <= 0 || height <= 0) {
      return;
    }
    const ratio = window.devicePixelRatio || 1;
    // 物理像素：窗口缩放和屏幕缩放都算在里面。再乘当前放大倍数，
    // 位图就始终和屏幕保持 1:1，而不是一直只有一张整体渲染的图。
    setSheet(renderTreemapSheet(
      layout,
      currentTheme,
      width * ratio,
      height * ratio,
      magnification));
  });

  /** 位图糊到这个倍数就该重渲一张更精细的。 */
  const sheetRefreshRatio = 1.3;

  // 缩放过程中不重渲（那是整张图重画，比一帧久得多），
  // 等视口停下来再补一张更精细的。停之前先用现有位图撑着，交互不受影响。
  let sheetTimer = 0;
  createEffect(() => {
    const currentViewport = viewport();
    const layout = props.layout;
    const wanted = magnificationOf(layout, currentViewport.scale);
    const current = sheetMagnification();
    if (wanted < current * sheetRefreshRatio && wanted > current / sheetRefreshRatio) {
      return;
    }
    if (sheetTimer !== 0) {
      window.clearTimeout(sheetTimer);
    }
    sheetTimer = window.setTimeout(() => {
      sheetTimer = 0;
      setSheetMagnification(magnificationOf(props.layout, viewport().scale));
    }, 140);
  });

  // 换了布局就回到 1:1：新布局覆盖的正是当前看得见的那块。
  createEffect(() => {
    void props.layout;
    setSheetMagnification(1);
  });

  onCleanup(() => {
    if (sheetTimer !== 0) {
      window.clearTimeout(sheetTimer);
      sheetTimer = 0;
    }
  });

  createEffect(() => {
    const { width, height } = size();
    if (width <= 0 || height <= 0) {
      return;
    }
    pending = {
      layout: props.layout,
      sheet: sheet(),
      viewport: viewport(),
      width,
      height,
      devicePixelRatio: window.devicePixelRatio || 1,
      theme: theme(),
      selectedNodeId: props.selectedNodeId,
      labelOf: props.labelOf
    };
    if (paintHandle !== 0) {
      return;
    }
    paintHandle = window.requestAnimationFrame(() => {
      paintHandle = 0;
      const options = pending;
      pending = null;
      const context = canvas?.getContext("2d");
      if (context && options) {
        paintTreemap(context, options);
      }
    });
  });

  onCleanup(() => {
    if (paintHandle !== 0) {
      window.cancelAnimationFrame(paintHandle);
      paintHandle = 0;
    }
  });

  // 滚轮和贴边平移会连着改很多次视口。等它停下来再报一次，
  // 不然每动一下都去要一份新布局。
  let viewTimer = 0;
  createEffect(() => {
    const current = viewport();
    const { width, height } = size();
    if (width <= 0 || height <= 0) {
      return;
    }
    const ratio = window.devicePixelRatio || 1;
    const rect = visibleUnitRect(current);
    if (viewTimer !== 0) {
      window.clearTimeout(viewTimer);
    }
    viewTimer = window.setTimeout(() => {
      viewTimer = 0;
      props.onViewChanged({
        // 物理像素：窗口缩放和屏幕缩放都已经算在里面了。
        pixelWidth: width * ratio,
        pixelHeight: height * ratio,
        scale: current.scale,
        ...rect
      });
    }, 160);
  });

  onCleanup(() => {
    if (viewTimer !== 0) {
      window.clearTimeout(viewTimer);
      viewTimer = 0;
    }
  });

  function measure() {
    if (!container || !canvas) {
      return;
    }
    const rect = container.getBoundingClientRect();
    const ratio = window.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.round(rect.width * ratio));
    canvas.height = Math.max(1, Math.round(rect.height * ratio));
    canvas.style.width = `${rect.width}px`;
    canvas.style.height = `${rect.height}px`;
    setSize({ width: rect.width, height: rect.height });
  }

  function localPoint(event: MouseEvent | WheelEvent) {
    const rect = canvas?.getBoundingClientRect();
    return rect
      ? { x: event.clientX - rect.left, y: event.clientY - rect.top }
      : { x: 0, y: 0 };
  }

  function nodeAt(event: MouseEvent) {
    const point = localPoint(event);
    const { width, height } = size();
    return hitTest(props.layout, viewport(), width, height, point.x, point.y);
  }

  /**
   * 贴边平移。只在指针确实落在边缘带里的时候才跑定时器，一离开就停。
   *
   * 先前是只要动过鼠标就一直跑，只有 mouseleave 才停 —— 于是指针没触发
   * mouseleave 就离开画布时（弹出菜单盖上来、窗口失焦、指针飞出去），
   * 视图会自己一直平移，怎么点都退不出来。现在它自己会停。
   */
  function updateEdgePan() {
    const { width, height } = size();
    if (pointerX < 0 || width <= 0) {
      stopEdgePan();
      return;
    }
    const delta = edgePanDelta(pointerX, pointerY, width, height);
    if (delta.deltaX === 0 && delta.deltaY === 0) {
      stopEdgePan();
      return;
    }
    if (edgeTimer !== 0) {
      return;
    }
    edgeTimer = window.setInterval(() => {
      const current = size();
      if (pointerX < 0 || current.width <= 0) {
        stopEdgePan();
        return;
      }
      const step = edgePanDelta(pointerX, pointerY, current.width, current.height);
      if (step.deltaX === 0 && step.deltaY === 0) {
        stopEdgePan();
        return;
      }
      setViewport((viewportNow) => panByPixels(
        viewportNow,
        current.width,
        current.height,
        step.deltaX,
        step.deltaY));
    }, 16);
  }

  function stopEdgePan() {
    if (edgeTimer !== 0) {
      window.clearInterval(edgeTimer);
      edgeTimer = 0;
    }
  }

  return (
    <div
      class="disk-usage-treemap"
      ref={(element) => { container = element; }}
    >
      <canvas
        ref={(element) => { canvas = element; }}
        class="disk-usage-treemap-canvas"
        tabIndex={0}
        role="img"
        onWheel={(event) => {
          event.preventDefault();
          const point = localPoint(event);
          const { width, height } = size();
          // 每格滚轮 1.2 倍，向上放大、向下缩小。
          const factor = event.deltaY < 0 ? 1.2 : 1 / 1.2;
          setViewport((current) =>
            zoomAt(current, width, height, point.x, point.y, factor));
        }}
        onMouseEnter={(event) => {
          // 指针进来就接管键盘，省掉"先点一下才能用方向键"这一步。
          // 正在输入的时候不抢：鼠标扫过图上不该把光标从输入框里拽走。
          if (isTypingSomewhere()) {
            return;
          }
          // preventScroll：这张图可能在页面下半部分，对焦不该把页面滚过去。
          event.currentTarget.focus({ preventScroll: true });
        }}
        onMouseMove={(event) => {
          const point = localPoint(event);
          pointerX = point.x;
          pointerY = point.y;
          updateEdgePan();
          props.onHover(nodeAt(event), event.clientX, event.clientY);
        }}
        onMouseLeave={() => {
          pointerX = -1;
          pointerY = -1;
          stopEdgePan();
          props.onHover(-1, 0, 0);
        }}
        onClick={(event) => {
          const node = nodeAt(event);
          if (node >= 0) {
            props.onSelect(node);
          }
        }}
        onDblClick={(event) => {
          const node = nodeAt(event);
          if (node >= 0) {
            props.onActivate(node);
          }
        }}
        onContextMenu={(event) => {
          event.preventDefault();
          const node = nodeAt(event);
          if (node >= 0) {
            props.onSelect(node);
            props.onContextMenu(node, event.clientX, event.clientY);
          }
        }}
        onKeyDown={(event) => {
          const { width, height } = size();
          const step = 60;
          switch (event.key) {
            case "ArrowLeft":
            case "ArrowRight":
            case "ArrowUp":
            case "ArrowDown":
              event.preventDefault();
              // 方向键先换选中项；没有选中项时退化成平移视图。
              if (props.selectedNodeId >= 0) {
                props.onSelect(neighbourOf(
                  props.layout,
                  props.selectedNodeId,
                  event.key));
                return;
              }
              // 按右就往右看，和贴边平移同一个方向口径。
              setViewport((current) => panByPixels(
                current,
                width,
                height,
                event.key === "ArrowRight" ? step : event.key === "ArrowLeft" ? -step : 0,
                event.key === "ArrowDown" ? step : event.key === "ArrowUp" ? -step : 0));
              return;
            case "Enter":
              if (props.selectedNodeId >= 0) {
                event.preventDefault();
                props.onActivate(props.selectedNodeId);
              }
              return;
            case "Home":
              event.preventDefault();
              setViewport(clampToBounds(identityViewport));
              return;
            default:
          }
        }}
      />
    </div>
  );
}

/**
 * 方向键换选中项：在同一张图里找那个方向上最近的方格。
 * 按中心点距离挑，并要求它确实在那个方向上，免得跳到身后去。
 */
function neighbourOf(
  layout: DiskUsageLayout,
  selectedNodeId: number,
  key: string
): number {
  let originIndex = -1;
  for (let index = 0; index < layout.nodeIds.length; index++) {
    if (layout.nodeIds[index] === selectedNodeId) {
      originIndex = index;
      break;
    }
  }
  if (originIndex < 0) {
    return selectedNodeId;
  }

  const originX = layout.x[originIndex] + layout.width[originIndex] / 2;
  const originY = layout.y[originIndex] + layout.height[originIndex] / 2;
  let best = selectedNodeId;
  let bestDistance = Number.POSITIVE_INFINITY;

  for (let index = 0; index < layout.nodeIds.length; index++) {
    if (index === originIndex || layout.depths[index] === 0) {
      continue;
    }
    const centreX = layout.x[index] + layout.width[index] / 2;
    const centreY = layout.y[index] + layout.height[index] / 2;
    const deltaX = centreX - originX;
    const deltaY = centreY - originY;
    const forward = key === "ArrowLeft"
      ? -deltaX
      : key === "ArrowRight"
        ? deltaX
        : key === "ArrowUp"
          ? -deltaY
          : deltaY;
    if (forward <= 0) {
      continue;
    }
    // 偏离主方向的部分加权，避免选到斜对角那些其实很远的格子。
    const lateral = key === "ArrowLeft" || key === "ArrowRight"
      ? Math.abs(deltaY)
      : Math.abs(deltaX);
    const distance = forward + lateral * 2;
    if (distance < bestDistance) {
      bestDistance = distance;
      best = layout.nodeIds[index];
    }
  }
  return best;
}

/**
 * 焦点现在是不是在一个正在输入的地方。
 * 悬停对焦要避开这些，不然鼠标随便扫过去就会把用户的输入光标弄丢。
 */
function isTypingSomewhere(): boolean {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement)) {
    return false;
  }
  if (active.isContentEditable) {
    return true;
  }
  const tag = active.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

function readPaintTheme(): DiskUsagePaintTheme {
  const theme = readDocumentTheme();
  // 「跟随系统」要问系统，不能当成浅色，否则深色模式下整张图会白得刺眼。
  const dark = theme === "dark"
    || theme === "lowContrast"
    || (theme === "system"
      && window.matchMedia?.("(prefers-color-scheme: dark)").matches === true);
  return {
    directoryHue: 168,
    fileHue: 205,
    surface: dark ? "#10161a" : "#f2f6f7",
    label: dark ? "#f2f6f7" : "#10161a",
    labelShadow: dark ? "rgba(6, 10, 12, 0.62)" : "rgba(255, 255, 255, 0.72)",
    selection: dark ? "#7ae6cf" : "#0b7a66"
  };
}
