import { observeLayout, type LayoutSize } from "./layoutObservation.ts";
import { createEffect, on, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { Presentation } from "../protocol/runtime.ts";
import { selectionIndicatorDefinition } from "../components/selectionIndicator.ts";
import { interactionAnimationMode, interactionRuntime, useInteraction } from "./lodestone.ts";
import { createOrderHost } from "./orderHost.ts";

/* Capture the current indicator box before retargeting, then interpolate to the selected item in group content coordinates. Fade when either endpoint is absent; text color follows covered area. Layout changes refresh the destination. */

interface Box { readonly left: number; readonly top: number; readonly width: number; readonly height: number }

export interface SelectionIndicatorOptions {
  /** Runtime trace name. */
  readonly name: string;
  /** Positioned group containing the indicator; item boxes use its content coordinates. */
  readonly container: () => HTMLElement | undefined;
  readonly indicator: () => HTMLElement | undefined;
  /** Currently selected item, or null when none is selected. */
  readonly selected: () => HTMLElement | null;
  /** Group items whose text color follows indicator coverage. */
  readonly items: () => readonly HTMLElement[];
  /** Selected item order. */
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
  // The shared observer calls this read-only measurement phase after layout.
  const measure = (sizes: ReadonlyMap<Element, LayoutSize>) => {
    const container = options.container();
    if (!container) return;
    const base = container.getBoundingClientRect();
    // Keep hidden-group geometry until ResizeObserver delivers the visible layout.
    if (!base.width || !base.height) return;
    // Cache group-local layout coordinates; borderBoxSize excludes ancestor entry scaling.
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
    // Disabled animation modes fill the selected item with absolute positioning and skip measurement.
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
    // Only animated modes need geometry. Reobserve for an initial post-layout notification without forcing layout.
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

/** Fraction of the item area covered by the indicator. */
function overlap(a: Box, b: Box): number {
  const w = Math.min(a.left + a.width, b.left + b.width) - Math.max(a.left, b.left);
  const h = Math.min(a.top + a.height, b.top + b.height) - Math.max(a.top, b.top);
  if (w <= 0 || h <= 0 || b.width <= 0 || b.height <= 0) return 0;
  return Math.min(1, (w * h) / (b.width * b.height));
}
