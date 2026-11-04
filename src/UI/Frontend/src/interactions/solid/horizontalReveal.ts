import { createEffect, onCleanup, onMount, type Accessor } from "solid-js";
import { observeLayout } from "./layoutObservation.ts";

/** Read after layout using the same read/write phases as the selection indicator. */
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
