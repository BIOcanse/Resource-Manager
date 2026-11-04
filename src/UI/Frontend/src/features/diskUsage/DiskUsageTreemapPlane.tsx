import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds.ts";
import { useWhileNeeded } from "../../frontendWork/useWhileNeeded.ts";
import { wheelNotches } from "../../interactions/wheelNotches.ts";
import { uiText } from "../../text.ts";
import { observeDocumentAppearance } from "../../presentation/documentAppearance";
import { readMotionDurationMs, readMotionEase } from "../../presentation/motionTokens.ts";
import { accumulateWheelZoom, wheelZoomAt, type DiskUsageWheelZoom } from "./diskUsageWheelZoom.ts";
import {
  hitTest,
  visibleNodeCentre,
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
  type DiskUsageViewport,
  type DiskUsageViewWindow
} from "./diskUsageViewport.ts";
import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";

/** Map fixed unit-space layout rectangles to canvas pixels; pan, zoom, and selection stay local. */
export function DiskUsageTreemapPlane(props: {
  layout: DiskUsageLayout;
  selectedNodeId: number;
  labelOf: (nodeId: number) => string;
  onSelect: (nodeId: number) => void;
  onActivate: (nodeId: number) => void;
  onContextMenu: (nodeId: number, clientX: number, clientY: number) => void;
  onHover: (nodeId: number, clientX: number, clientY: number) => void;
  /** Report settled bounds and physical size so the owner can request finer local layout. */
  onViewChanged: (view: DiskUsageViewWindow) => void;
  /** Changing this value resets the viewport to the full map. */
  resetNonce: number;
}) {
  let canvas: HTMLCanvasElement | undefined;
  let container: HTMLDivElement | undefined;
  const [viewport, setViewport] = createSignal<DiskUsageViewport>(identityViewport);
  const [size, setSize] = createSignal({ width: 0, height: 0 });
  // Reuse identical theme colors to avoid unnecessary bitmap rerenders.
  const [theme, setTheme] = createSignal(readPaintTheme(), { equals: samePaintTheme });

  // Edge panning runs only while the pointer remains in the edge region.
  let edgeFrame = 0;
  let edgeLastTime = 0;
  let pointerX = -1;
  let pointerY = -1;

  // Null means no wheel transition; active transitions set the viewport each frame.
  let wheelZoom: DiskUsageWheelZoom | null = null;
  let wheelZoomEase: (progress: number) => number = (progress) => progress;
  let wheelZoomFrame = 0;

  function stepWheelZoom(now: number) {
    wheelZoomFrame = 0;
    if (!wheelZoom) {
      return;
    }
    const frame = wheelZoomAt(wheelZoom, now, wheelZoomEase);
    setViewport(frame.viewport);
    if (frame.done) {
      wheelZoom = null;
      return;
    }
    wheelZoomFrame = window.requestAnimationFrame(stepWheelZoom);
  }

  /** Direct viewport operations stop an active transition at its current position. */
  function stopWheelZoom() {
    wheelZoom = null;
    if (wheelZoomFrame !== 0) {
      window.cancelAnimationFrame(wheelZoomFrame);
      wheelZoomFrame = 0;
    }
  }

  /** Pan both transition endpoints so the next frame cannot overwrite the movement. */
  function panView(deltaX: number, deltaY: number) {
    const { width, height } = size();
    if (wheelZoom) {
      wheelZoom = {
        ...wheelZoom,
        from: panByPixels(wheelZoom.from, width, height, deltaX, deltaY),
        to: panByPixels(wheelZoom.to, width, height, deltaX, deltaY)
      };
    }
    setViewport((current) => panByPixels(current, width, height, deltaX, deltaY));
  }

  // Stop edge panning and hide hover facts on blur or page leave.
  // No pointer updates are guaranteed while the page is inactive.
  const releasePointer = () => {
    pointerX = -1;
    pointerY = -1;
    stopEdgePan();
    props.onHover(-1, 0, 0);
  };
  // Stop zoom transitions on leave so hidden pages schedule no frames.
  onPageLeave(() => {
    releasePointer();
    stopWheelZoom();
  });

  onMount(() => {
    const observer = new ResizeObserver(() => measure());
    if (container) {
      observer.observe(container);
    }
    measure();

    onCleanup(() => {
      observer.disconnect();
      stopEdgePan();
      stopWheelZoom();
    });
  });

  // Refresh colors on entry, follow appearance changes, and release all listeners on exit.
  useWhileNeeded(frontendWorkIds.diskUsagePage, () => {
    const syncTheme = () => setTheme(readPaintTheme());
    syncTheme();
    const stopObservingAppearance = observeDocumentAppearance(syncTheme);
    window.addEventListener("blur", releasePointer);
    return () => {
      window.removeEventListener("blur", releasePointer);
      stopObservingAppearance();
    };
  });

  // Reset to the full map when the root changes.
  // Root identity determines viewport reset.
  // Refinement replaces layout objects without changing their root.
  // Preserve the viewport when detail alone changes.
  let shownRootNodeId = -1;
  createEffect(() => {
    const rootNodeId = props.layout.rootNodeId;
    if (rootNodeId === shownRootNodeId) {
      return;
    }
    shownRootNodeId = rootNodeId;
    stopWheelZoom();
    setViewport(identityViewport);
  });

  // Reset restores the full map even after panning off-canvas.
  let appliedResetNonce = props.resetNonce;
  createEffect(() => {
    const nonce = props.resetNonce;
    if (nonce === appliedResetNonce) {
      return;
    }
    appliedResetNonce = nonce;
    // Stop edge panning before reset so the next frame cannot undo it.
    // A later pointer move may resume edge panning.
    pointerX = -1;
    pointerY = -1;
    stopEdgePan();
    stopWheelZoom();
    setViewport(identityViewport);
  });

  // Coalesce redraw requests into the next animation frame.
  // Paint at most once per frame using the newest viewport.
  // Frequent edge-pan updates must not accumulate a render backlog.
  let paintHandle = 0;
  // New pending state replaces older pending state.
  // Always paint the latest requested viewport.
  let pending: DiskUsagePaintOptions | null = null;

  // Render tile geometry only when creating the cached bitmap.
  const [sheet, setSheet] = createSignal<DiskUsageSheet | null>(null);
  // Reuse the current bitmap until zoom exceeds its useful resolution.
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
    // Use physical pixels, including display and window scaling.
    // Apply magnification so the bitmap matches the visible screen detail.
    setSheet(renderTreemapSheet(
      layout,
      currentTheme,
      width * ratio,
      height * ratio,
      magnification));
  });

  /** Magnification threshold for rendering a sharper bitmap. */
  const sheetRefreshRatio = 1.3;

  // Defer expensive bitmap rendering until zoom settles.
  // Reuse the existing bitmap throughout interaction.
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

  // A new layout covers the current view and resets relative magnification.
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

  // Report the view once wheel zoom and edge pan settle.
  // Avoid requesting layout for every intermediate movement.
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
        // Physical dimensions already include display and window scaling.
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
    // CSS owns display size so the canvas cannot force its container wider.
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

  /** Schedule edge-pan frames only while the pointer is in the edge region; stop on exit or loss of pointer activity. */
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
    if (edgeFrame !== 0) {
      return;
    }
    edgeLastTime = performance.now();
    edgeFrame = window.requestAnimationFrame(edgePanFrame);
  }

  /** Move by elapsed time so edge-pan speed is independent of frame rate. */
  function edgePanFrame(now: number) {
    edgeFrame = 0;
    // Cap elapsed time after background suspension to avoid a large jump.
    const elapsedSeconds = Math.min(Math.max(now - edgeLastTime, 0), 100) / 1000;
    edgeLastTime = now;
    const current = size();
    if (pointerX < 0 || current.width <= 0) {
      return;
    }
    const step = edgePanDelta(pointerX, pointerY, current.width, current.height, undefined, undefined, elapsedSeconds);
    if (step.deltaX === 0 && step.deltaY === 0) {
      return;
    }
    panView(step.deltaX, step.deltaY);
    edgeFrame = window.requestAnimationFrame(edgePanFrame);
  }

  function stopEdgePan() {
    if (edgeFrame !== 0) {
      window.cancelAnimationFrame(edgeFrame);
      edgeFrame = 0;
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
        aria-label={uiText.diskUsage.treemapLabel}
        onWheel={(event) => {
          event.preventDefault();
          const notches = wheelNotches(event.deltaY, event.deltaMode);
          if (notches === 0) {
            return;
          }
          const point = localPoint(event);
          const { width, height } = size();
          // Accumulate wheel notches into the target and transition from the current view.
          // Zero-duration motion goes directly to the target; stepped easing remains stepped.
          const target = event.currentTarget;
          const durationMs = readMotionDurationMs(target, "--motion-fast");
          const zoom = accumulateWheelZoom(
            wheelZoom, viewport(), width, height, point.x, point.y, notches, performance.now(), durationMs);
          if (durationMs <= 0) {
            stopWheelZoom();
            setViewport(zoom.to);
            return;
          }
          wheelZoom = zoom;
          wheelZoomEase = readMotionEase(target, "--ease-decel");
          if (wheelZoomFrame === 0) {
            wheelZoomFrame = window.requestAnimationFrame(stepWheelZoom);
          }
        }}
        onMouseEnter={(event) => {
          // Focus on pointer entry so arrow navigation is immediately available.
          // Preserve focus in active text inputs during pointer hover.
          if (isTypingSomewhere()) {
            return;
          }
          // Prevent focus from scrolling the page to a lower canvas.
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
          // Keyboard menus open on keydown; handle only pointer menus here to avoid duplicate targets.
          if (event.button !== 2) {
            return;
          }
          const node = nodeAt(event);
          if (node >= 0) {
            props.onSelect(node);
            props.onContextMenu(node, event.clientX, event.clientY);
          }
        }}
        onKeyDown={(event) => {
          if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
            event.preventDefault();
            // Keyboard menus use the selected tile and require a selection.
            const selected = props.selectedNodeId;
            const rect = event.currentTarget.getBoundingClientRect();
            const centre = selected >= 0
              ? visibleNodeCentre(props.layout, viewport(), size().width, size().height, selected)
              : null;
            if (centre) {
              props.onContextMenu(selected, rect.left + centre.x, rect.top + centre.y);
            }
            return;
          }
          const step = 60;
          switch (event.key) {
            case "ArrowLeft":
            case "ArrowRight":
            case "ArrowUp":
            case "ArrowDown":
              event.preventDefault();
              // Arrow keys move selection; without a selection they pan.
              if (props.selectedNodeId >= 0) {
                props.onSelect(neighbourOf(
                  props.layout,
                  props.selectedNodeId,
                  event.key));
                return;
              }
              // Rightward navigation reveals content to the right, matching edge pan.
              panView(
                event.key === "ArrowRight" ? step : event.key === "ArrowLeft" ? -step : 0,
                event.key === "ArrowDown" ? step : event.key === "ArrowUp" ? -step : 0);
              return;
            case "Enter":
              if (props.selectedNodeId >= 0) {
                event.preventDefault();
                props.onActivate(props.selectedNodeId);
              }
              return;
            case "Home":
              event.preventDefault();
              stopWheelZoom();
              setViewport(clampToBounds(identityViewport));
              return;
            default:
          }
        }}
      />
    </div>
  );
}

/** Choose the nearest tile center in the requested direction. */
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
    // Weight cross-axis distance to discourage distant diagonal targets.
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

/** Detect active text entry so hover focus cannot steal the input caret. */
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

function samePaintTheme(left: DiskUsagePaintTheme, right: DiskUsagePaintTheme) {
  return left.directoryHue === right.directoryHue && left.fileHue === right.fileHue && left.surface === right.surface
    && left.label === right.label && left.labelShadow === right.labelShadow && left.selection === right.selection;
}

// Read theme CSS variables and the effective color-scheme.
// CSS media queries resolve system appearance.
function readPaintTheme(): DiskUsagePaintTheme {
  const style = getComputedStyle(document.documentElement);
  const token = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  const dark = style.colorScheme.includes("dark");
  return {
    directoryHue: Number(token("--treemap-directory-hue", "168")),
    fileHue: Number(token("--treemap-file-hue", "205")),
    surface: token("--surface", dark ? "#10161a" : "#f2f6f7"),
    label: token("--text", dark ? "#f2f6f7" : "#10161a"),
    labelShadow: dark ? "rgba(6, 10, 12, 0.62)" : "rgba(255, 255, 255, 0.72)",
    selection: token("--accent-strong", dark ? "#7ae6cf" : "#0b7a66")
  };
}
