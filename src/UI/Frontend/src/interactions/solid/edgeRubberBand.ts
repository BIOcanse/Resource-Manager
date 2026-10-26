import type { Instance, Presentation } from "../protocol/runtime.ts";
import { EDGE_BAND_RUBBER, edgeRubberBandDefinition } from "../components/edgeRubberBand.ts";
import { interactionRuntime } from "./lodestone.ts";
import { createOrderHost } from "./orderHost.ts";

/*
 * 滚动区边缘回弹的呈现（定义在 ../components/edgeRubberBand.ts）：整个应用一个实例，挂在文档上。
 *
 * - 滚轮（被动监听，不拦原生滚动）：从指针下的元素往外找能竖着滚的滚动区。这一串里还有能往这个方向滚的，就是原生滚动，不管；
 *   都到边了，最里面那个滚动区回弹：这一次往外滚的量累加成“越过的量”，按橡皮筋换成偏移，交给运行时做宿主值。
 *   反方向滚就清掉越过的量。
 * - 滚轮没有“松开”：最后一格之后 WHEEL_RELEASE_MS 没有新的一格，就当松开，宿主值回到 0，内容回到边缘。
 * - 画：滚动区上写 data-edge-band 和 --edge-band（偏移 px），styles/motion.css 让它的子元素平移这么多，滚动区本身、布局都不动。
 *   回到 0 时去掉这两项。一个滚动区还没回到边缘时，别的滚动区不开始回弹。
 * - 橡皮筋系数（参数 edgeBandRubber）为 0 时不回弹，也不读滚动区：只有流畅平滑在全局层把它设成复刻的值。
 */

/** 滚轮停了这么久算松开（毫秒）：比滚轮连续滚动时两格之间的间隔长，比人能察觉的停顿短。 */
const WHEEL_RELEASE_MS = 120;
/** 滚轮按行、按页给的量换成 px：一行 16 px，一页是视口高。 */
const LINE_PX = 16;

const stretch = (travel: number, d: number, c: number) => (1 - 1 / ((travel * c) / d + 1)) * d;

/** 从 target 往外的滚动串：还能往 dy 的方向滚就是原生滚动（null）；都到边了，返回最里面的滚动区。 */
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
