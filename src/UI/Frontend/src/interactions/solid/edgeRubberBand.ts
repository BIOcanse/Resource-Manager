import type { Instance, Presentation } from "../protocol/runtime.ts";
import { EDGE_BAND_RUBBER, edgeRubberBandDefinition } from "../components/edgeRubberBand.ts";
import { interactionRuntime } from "./lodestone.ts";
import { createOrderHost } from "./orderHost.ts";

/* One document listener preserves native scrolling until every ancestor reaches its edge. Only the innermost scroller overscrolls; idle wheel input returns its host offset to zero. Transform live children so hit regions follow. Zero strength skips scroll-area reads. */

/** Wheel idle interval in milliseconds, treated as release. */
const WHEEL_RELEASE_MS = 120;
/** Convert line/page wheel deltas to pixels using line height or viewport height. */
const LINE_PX = 16;

const stretch = (travel: number, d: number, c: number) => (1 - 1 / ((travel * c) / d + 1)) * d;

/** Return the innermost scroller only when no ancestor can scroll in the requested direction. */
function edgeScroller(target: EventTarget | null, dy: number): HTMLElement | null {
  let element = target instanceof Element ? target : null;
  let innermost: HTMLElement | null = null;
  while (element && element !== element.ownerDocument.documentElement) {
    if (element instanceof HTMLElement && element.scrollHeight > element.clientHeight) {
      const style = getComputedStyle(element);
      if (style.overflowY === "auto" || style.overflowY === "scroll") {
        const canScroll = dy < 0 ? element.scrollTop > 0 : element.scrollTop + element.clientHeight < element.scrollHeight - 1;
        if (canScroll) return null;
        innermost ??= element;
        if (style.overscrollBehaviorY !== "auto") break;
      }
    }
    element = element.parentElement;
  }
  return innermost;
}

export function installEdgeRubberBand(document: Document): () => void {
  const host = createOrderHost(0);
  let element: HTMLElement | null = null;
  let travel = 0;
  let release: ReturnType<typeof setTimeout> | undefined;
  const clear = () => {
    if (!element) return;
    element.style.removeProperty("--edge-band");
    delete element.dataset.edgeBand;
    element = null;
  };
  const draw = (x: number) => {
    if (!element) return;
    if (x === 0 && host.host.value() === 0) { clear(); return; }
    element.dataset.edgeBand = "";
    element.style.setProperty("--edge-band", `${x.toFixed(2)}px`);
  };
  const view: Presentation = {
    element: document.body,
    bindingLabel: "滚动区子元素的平移",
    geometry: () => null,
    render: (d) => draw(d.x as number),
    aria: () => ""
  };
  const runtime = interactionRuntime();
  const instance: Instance = runtime.add("滚动区边缘回弹", edgeRubberBandDefinition, view, host.host);
  const letGo = () => {
    release = undefined;
    travel = 0;
    host.set(0, "滚轮停了");
  };
  const onWheel = (event: WheelEvent) => {
    const c = instance.params[EDGE_BAND_RUBBER].value;
    if (!(c > 0) || event.ctrlKey || event.deltaY === 0 || Math.abs(event.deltaX) > Math.abs(event.deltaY)) return;
    const dy = event.deltaY * (event.deltaMode === 1 ? LINE_PX : event.deltaMode === 2 ? window.innerHeight : 1);
    const scroller = edgeScroller(event.target, dy);
    if (!scroller || (element && scroller !== element)) {
      if (travel !== 0) letGo();
      return;
    }
    element = scroller;
    if (travel !== 0 && Math.sign(travel) !== Math.sign(dy)) travel = 0;
    travel += dy;
    host.set(-Math.sign(travel) * stretch(Math.abs(travel), scroller.clientHeight, c), "滚轮越过边缘");
    if (release !== undefined) clearTimeout(release);
    release = setTimeout(letGo, WHEEL_RELEASE_MS);
  };
  document.addEventListener("wheel", onWheel, { passive: true, capture: true });
  return () => {
    document.removeEventListener("wheel", onWheel, { capture: true });
    if (release !== undefined) clearTimeout(release);
    runtime.remove(instance, performance.now());
    clear();
  };
}
