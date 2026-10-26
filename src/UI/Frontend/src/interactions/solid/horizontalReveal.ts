import { createEffect, onCleanup, onMount, type Accessor } from "solid-js";
import { observeLayout } from "./layoutObservation.ts";

/** 页签横向滚动定位：在浏览器布局后读，和指示条共享读/写边界，不在切页微任务里量新页面。 */
export function useHorizontalReveal(options: {
  container: () => HTMLElement | undefined;
  selected: () => HTMLElement | null;
  change: Accessor<unknown>;
}): void {
  onMount(() => {
    let scroll: { element: HTMLElement; left: number } | null = null;
    const layout = observeLayout({
      read: (sizes) => {
        scroll = null;
        const container = options.container();
        const selected = options.selected();
        if (!container || !selected) return;
        const viewport = container.getBoundingClientRect();
        if (!viewport.width || !viewport.height) return;
        const target = selected.getBoundingClientRect();
        const delta = target.left < viewport.left ? target.left - viewport.left
          : target.right > viewport.right ? target.right - viewport.right : 0;
        if (delta) scroll = { element: container, left: container.scrollLeft + delta * sizes.get(container)!.width / viewport.width };
      },
      write: () => { if (scroll) scroll.element.scrollLeft = scroll.left; }
    });
    createEffect(() => {
      options.change();
      const container = options.container();
      const selected = options.selected();
      layout.update(container && selected ? [container, selected] : []);
    });
    onCleanup(() => layout.dispose());
  });
}
