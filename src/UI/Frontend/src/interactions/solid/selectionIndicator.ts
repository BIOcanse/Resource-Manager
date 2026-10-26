import { createEffect, on, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { Presentation } from "../protocol/runtime.ts";
import { selectionIndicatorDefinition } from "../components/selectionIndicator.ts";
import { interactionRuntime, useInteraction } from "./lodestone.ts";
import { createOrderHost } from "./orderHost.ts";

/*
 * 选中指示条的呈现（定义在 ../components/selectionIndicator.ts）。docs/page_motion_2026-10-25.md 第 2 节。
 *
 * 指示条是组里的一块（.selection-indicator），带着“选中”的样子（底色、边框、投影），从一项滑到另一项。
 * 一段：选中项变了，记下这一刻画到的框和不透明度（起点），新选中的项的框是终点；力场把序号从起点带到终点，
 * 走了多少（0 到 1）就在两个框之间插出位置和大小。没有起点（之前没有选中项）就在终点淡入，没有终点就在起点淡出。
 * 每一项的文字颜色按指示条盖住它多少混合（--tab-cover，0 到 1），所以文字跟着指示条变色，不在点下去的一刻跳。
 * 框按组的内容坐标量（随组滚动）。组或项的大小、项的文字变了就重量终点，下一次画按新终点。
 */

interface Box { readonly left: number; readonly top: number; readonly width: number; readonly height: number }

export interface SelectionIndicatorOptions {
  /** 运行时里的名字（日志）。 */
  readonly name: string;
  /** 组：定位元素，指示条是它的子元素，项的框按它的内容坐标量。 */
  readonly container: () => HTMLElement | undefined;
  readonly indicator: () => HTMLElement | undefined;
  /** 现在选中的项；没有选中项（例如这一页没有二级分页）为 null。 */
  readonly selected: () => HTMLElement | null;
  /** 组里的项：文字颜色按指示条盖住多少混合。 */
  readonly items: () => readonly HTMLElement[];
  /** 选中项的序号（分页树或列表里的次序）。 */
  readonly order: Accessor<number>;
}

export function useSelectionIndicator(options: SelectionIndicatorOptions): void {
  const orderHost = createOrderHost(options.order());
  let from: { box: Box | null; opacity: number; order: number } = { box: null, opacity: 0, order: options.order() };
  let to: { box: Box | null; order: number } = { box: null, order: options.order() };
  let drawn: { box: Box | null; opacity: number; x: number } = { box: null, opacity: 0, x: options.order() };
  let items: Array<{ element: HTMLElement; box: Box; cover: number }> = [];
  let stale = true;

  const measure = () => {
    const container = options.container();
    if (!container) { stale = false; to = { box: null, order: to.order }; items = []; return; }
    // 组隐藏着（例如外壳里不是当前页的那组二级分页）量不出框：留着上次量的，组再显示时（大小变了）重量。
    if (container.getClientRects().length === 0) { stale = true; return; }
    stale = false;
    const base = container.getBoundingClientRect();
    const boxOf = (element: HTMLElement): Box => {
      const r = element.getBoundingClientRect();
      return {
        left: r.left - base.left - container.clientLeft + container.scrollLeft,
        top: r.top - base.top - container.clientTop + container.scrollTop,
        width: r.width,
        height: r.height
      };
    };
    const selected = options.selected();
    to = { box: selected && selected.isConnected ? boxOf(selected) : null, order: to.order };
    const previous = new Map(items.map((item) => [item.element, item.cover]));
    items = options.items().map((element) => ({ element, box: boxOf(element), cover: previous.get(element) ?? -1 }));
  };

  const draw = (x: number) => {
    const span = to.order - from.order;
    const p = span === 0 ? 1 : Math.min(1, Math.max(0, (x - from.order) / span));
    if (p > 0 && stale) measure();
    let box: Box | null;
    let opacity: number;
    if (from.box && to.box) {
      box = lerpBox(from.box, to.box, p);
      opacity = from.opacity + (1 - from.opacity) * p;
    } else if (to.box) {
      box = to.box;
      opacity = p;
    } else if (from.box) {
      box = from.box;
      opacity = from.opacity * (1 - p);
    } else {
      box = null;
      opacity = 0;
    }
    drawn = { box, opacity, x };
    const element = options.indicator();
    if (element) {
      element.style.opacity = String(opacity);
      element.style.visibility = box && opacity > 0 ? "visible" : "hidden";
      if (box) {
        element.style.transform = `translate(${box.left}px, ${box.top}px)`;
        element.style.width = `${box.width}px`;
        element.style.height = `${box.height}px`;
      }
    }
    for (const item of items) {
      const cover = box ? Math.round(overlap(box, item.box) * opacity * 1000) / 1000 : 0;
      if (cover !== item.cover) {
        item.cover = cover;
        item.element.style.setProperty("--tab-cover", String(cover));
      }
    }
  };

  const view = (): Presentation => ({
    element: options.indicator(),
    bindingLabel: "位置、大小和文字颜色",
    geometry: () => null,
    render: (d) => draw(d.x),
    aria: () => ""
  });
  const handle = useInteraction(options.name, selectionIndicatorDefinition, view, orderHost.host);

  onMount(() => {
    measure();
    from = { box: to.box, opacity: to.box ? 1 : 0, order: options.order() };
    draw(options.order());
    // 组、项的大小或项的文字变了：终点要重量。
    // 大小变了在 ResizeObserver 里当场重量重画：它在这一帧布局之后、绘制之前回调，隐藏的组显示出来的第一帧指示条就在对的地方。
    // 增删项、改文字等到下一帧，几次变化并成一次。
    const remeasureNow = () => {
      measure();
      observeItems();
      draw(drawn.x);
    };
    let scheduled = false;
    const remeasure = () => {
      if (scheduled) return;
      scheduled = true;
      requestAnimationFrame(() => {
        scheduled = false;
        remeasureNow();
      });
    };
    const resize = new ResizeObserver(remeasureNow);
    // 只在组或项换了的时候重新观察：每次 observe 都会先回调一次，无条件重来就成了每帧一次的循环。
    let observed: readonly Element[] = [];
    const observeItems = () => {
      const container = options.container();
      const next = [...(container ? [container] : []), ...items.map((item) => item.element)];
      if (next.length === observed.length && next.every((element, index) => element === observed[index])) return;
      observed = next;
      resize.disconnect();
      for (const element of next) resize.observe(element);
    };
    observeItems();
    const mutation = new MutationObserver(remeasure);
    const container = options.container();
    if (container) mutation.observe(container, { childList: true, subtree: true, characterData: true });
    onCleanup(() => {
      resize.disconnect();
      mutation.disconnect();
    });
  });

  createEffect(on(options.order, (next) => {
    const instance = handle.instance;
    if (!instance) return;
    from = { box: drawn.box, opacity: drawn.opacity, order: drawn.x };
    to = { box: to.box, order: next };
    stale = true;
    orderHost.set(next, "选中项变了");
    draw(interactionRuntime().driveOf(instance, performance.now()).x as number);
  }, { defer: true }));
}

function lerpBox(a: Box, b: Box, p: number): Box {
  return {
    left: a.left + (b.left - a.left) * p,
    top: a.top + (b.top - a.top) * p,
    width: a.width + (b.width - a.width) * p,
    height: a.height + (b.height - a.height) * p
  };
}

/** 指示条盖住这一项的面积占这一项的多少。 */
function overlap(a: Box, b: Box): number {
  const w = Math.min(a.left + a.width, b.left + b.width) - Math.max(a.left, b.left);
  const h = Math.min(a.top + a.height, b.top + b.height) - Math.max(a.top, b.top);
  if (w <= 0 || h <= 0 || b.width <= 0 || b.height <= 0) return 0;
  return Math.min(1, (w * h) / (b.width * b.height));
}
