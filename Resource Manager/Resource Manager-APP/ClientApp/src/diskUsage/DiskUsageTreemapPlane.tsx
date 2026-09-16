import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { readDocumentTheme } from "../presentation/documentTheme";
import { hitTest, paintTreemap, type DiskUsagePaintTheme } from "./diskUsageTreemapPaint.ts";
import {
  clampToBounds,
  edgePanDelta,
  identityViewport,
  panByPixels,
  zoomAt,
  type DiskUsageViewport
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

    onCleanup(() => {
      observer.disconnect();
      themeObserver.disconnect();
      stopEdgePan();
    });
  });

  // 换了一棵树就回到整图视角，否则会停在上一棵树的某个角落。
  createEffect(() => {
    void props.layout;
    setViewport(identityViewport);
  });

  createEffect(() => {
    const context = canvas?.getContext("2d");
    const { width, height } = size();
    if (!context || width <= 0 || height <= 0) {
      return;
    }
    paintTreemap(context, {
      layout: props.layout,
      viewport: viewport(),
      width,
      height,
      devicePixelRatio: window.devicePixelRatio || 1,
      theme: theme(),
      selectedNodeId: props.selectedNodeId,
      labelOf: props.labelOf
    });
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

  function startEdgePan() {
    if (edgeTimer !== 0) {
      return;
    }
    edgeTimer = window.setInterval(() => {
      const { width, height } = size();
      if (pointerX < 0 || width <= 0) {
        stopEdgePan();
        return;
      }
      const delta = edgePanDelta(pointerX, pointerY, width, height);
      if (delta.deltaX === 0 && delta.deltaY === 0) {
        return;
      }
      setViewport((current) =>
        panByPixels(current, width, height, delta.deltaX, delta.deltaY));
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
        onMouseMove={(event) => {
          const point = localPoint(event);
          pointerX = point.x;
          pointerY = point.y;
          startEdgePan();
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
              setViewport((current) => panByPixels(
                current,
                width,
                height,
                event.key === "ArrowLeft" ? step : event.key === "ArrowRight" ? -step : 0,
                event.key === "ArrowUp" ? step : event.key === "ArrowDown" ? -step : 0));
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
