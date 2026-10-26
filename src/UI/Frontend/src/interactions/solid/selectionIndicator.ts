import { observeLayout, type LayoutSize } from "./layoutObservation.ts";
import { createEffect, on, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { Presentation } from "../protocol/runtime.ts";
import { selectionIndicatorDefinition } from "../components/selectionIndicator.ts";
import { interactionAnimationMode, interactionRuntime, useInteraction } from "./lodestone.ts";
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
  let measured = false;
  let selected: HTMLElement | null = null;
  const COVER_PRECISION = 1000;
  const TARGET_CLASS = "selection-indicator-target";

  const select = () => {
    const next = options.selected();
    if (selected !== next) {
      selected?.classList.remove(TARGET_CLASS);
      next?.classList.add(TARGET_CLASS);
      selected = next;
    }
    const indicator = options.indicator();
    const parent = interactionAnimationMode() === "normal" ? options.container() : selected ?? options.container();
    if (indicator && parent && indicator.parentElement !== parent) parent.appendChild(indicator);
    to = { box: items.find((item) => item.element === next)?.box ?? null, order: to.order };
  };
  // 唯一的量框处，由共享观察者在布局后的读阶段调用。读取不写 DOM。
  const measure = (sizes: ReadonlyMap<Element, LayoutSize>) => {
    const container = options.container();
    if (!container) return;
    const base = container.getBoundingClientRect();
    // 隐藏的常驻分页组保留缓存，重新显示时 ResizeObserver 会交付新的布局。
    if (!base.width || !base.height) return;
    // RO 的 borderBoxSize 不带祖先入场缩放，缓存必须是组内布局坐标。
    const size = sizes.get(container)!;
    const scaleX = base.width / size.width;
    const scaleY = base.height / size.height;
    const left = container.clientLeft - container.scrollLeft;
    const top = container.clientTop - container.scrollTop;
    const previous = new Map(items.map((item) => [item.element, item.cover]));
    items = options.items().map((element) => {
      const r = element.getBoundingClientRect();
      return { element, box: { left: (r.left - base.left) / scaleX - left, top: (r.top - base.top) / scaleY - top, width: r.width / scaleX, height: r.height / scaleY }, cover: previous.get(element) ?? -1 };
    });
    to = { box: items.find((item) => item.element === options.selected())?.box ?? null, order: to.order };
    if (!measured) {
      from = { box: to.box, opacity: to.box ? 1 : 0, order: to.order };
      measured = true;
    }
  };
  const draw = (x: number) => {
    // none/ultra 把同一块指示条放入选中项，由绝对定位铺满边框盒；不量框。
    if (interactionAnimationMode() !== "normal") {
      drawn = { box: null, opacity: selected ? 1 : 0, x };
      for (const item of options.items()) item.style.removeProperty("--tab-cover");
      return;
    }
    const span = to.order - from.order;
    const p = span === 0 ? 1 : Math.min(1, Math.max(0, (x - from.order) / span));
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
      const cover = box ? Math.round(overlap(box, item.box) * opacity * COVER_PRECISION) / COVER_PRECISION : 0;
      if (cover !== item.cover) {
        item.cover = cover;
        item.element.style.setProperty("--tab-cover", String(cover));
      }
    }
  };
  const view = (): Presentation => ({
    element: options.indicator(), bindingLabel: "位置、大小和文字颜色",
    geometry: () => null, render: (d) => draw(d.x), aria: () => ""
  });
  const handle = useInteraction(options.name, selectionIndicatorDefinition, view, orderHost.host);

  onMount(() => {
    select();
    const layout = observeLayout({ read: measure, write: () => draw(drawn.x) });
    const targets = () => {
      const container = options.container();
      return container ? [container, ...options.items()] : [];
    };
    // 仅正常动画需要缓存几何。重订阅获得下一次浏览器布局的初始通知，不强制布局。
    createEffect(on(interactionAnimationMode, (mode) => {
      const normal = mode === "normal";
      measured = false;
      items = [];
      layout.update(normal ? targets() : []);
      select();
      if (!normal) draw(options.order());
    }));
    const mutation = new MutationObserver(() => {
      select();
      if (interactionAnimationMode() !== "normal") draw(options.order());
      else {
        layout.update([]);
        layout.update(targets());
      }
    });
    const container = options.container();
    if (container) mutation.observe(container, { childList: true, subtree: true, characterData: true });
    onCleanup(() => { layout.dispose(); mutation.disconnect(); });
  });

  createEffect(on(options.order, (next) => {
    const instance = handle.instance;
    if (!instance) return;
    from = { box: drawn.box, opacity: drawn.opacity, order: drawn.x };
    to = { box: to.box, order: next };
    select();
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
