import { onCleanup } from "solid-js";
import type { Accessor } from "solid-js";
import { REORDER_BOUND, reorderItemDefinition } from "../components/reorderItem.ts";
import type { Host, Presentation } from "../protocol/runtime.ts";
import { ZERO } from "../protocol/vec.ts";
import type { Vec } from "../protocol/vec.ts";
import { useInteraction } from "./lodestone.ts";

/* The group maps pointer coordinates to item IDs and commits only to another item in the same group. Native controls keep browser input. Cleanup removes drag visuals when the runtime ends an unmounted gesture without a host call. */

/** Enabled native controls keep browser input instead of starting a drag. */
export const NATIVE_CONTROL = "button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href],[contenteditable='true']";

export interface ReorderGroup {
  /** Register an item in component initialization; disabled items decline routing. Cleanup unregisters it. */
  item(options: { id: Accessor<string>; enabled: Accessor<boolean> }): (element: HTMLElement) => void;
}

/** name labels runtime traces; commit fires once when release targets another group item. */
export function createReorderGroup(name: string, commit: (sourceId: string, targetId: string) => void): ReorderGroup {
  const ids = new Map<HTMLElement, Accessor<string>>();
  let source: HTMLElement | null = null;
  let target: HTMLElement | null = null;

  const setTarget = (next: HTMLElement | null) => {
    if (next === target) return;
    target?.classList.remove("drag-over");
    next?.classList.add("drag-over");
    target = next;
  };
  const end = () => {
    setTarget(null);
    source = null;
    document.body.classList.remove("reordering");
  };
  /** Other group item under the pointer, or null for the source or outside the group. */
  const itemAt = (point: Vec, from: HTMLElement): HTMLElement | null => {
    const hit = document.elementFromPoint(point.x, point.y);
    if (!hit) return null;
    for (const element of ids.keys()) {
      if (element.contains(hit)) return element === from ? null : element;
    }
    return null;
  };

  return {
    item(options) {
      let element!: HTMLElement;
      const view = (): Presentation<Vec> => ({
        element,
        bindingLabel: "源半透明（按模式）",
        // Geometry and channel endpoints share screen-pixel units.
        geometry: () => ({
          center: "指针（排序项不跟手）",
          atMin: { x: -REORDER_BOUND, y: -REORDER_BOUND },
          atMax: { x: REORDER_BOUND, y: REORDER_BOUND }
        }),
        nativeControl: (target) => {
          const control = target instanceof Element ? target.closest(NATIVE_CONTROL) : null;
          return control !== null && control !== element && element.contains(control);
        },
        render: ({ mode }) => { element.classList.toggle("drag-source", mode === "拖动"); },
        aria: () => ""
      });
      const host: Host<Vec> = {
        value: () => ZERO,
        enabled: options.enabled,
        call: (port, args) => {
          if (port === "over") {
            if (source !== element) {
              source = element;
              document.body.classList.add("reordering");
            }
            setTarget(itemAt(args[0] as Vec, element));
            return;
          }
          if (port === "commit") {
            const to = itemAt(args[0] as Vec, element);
            end();
            if (to) commit(options.id(), ids.get(to)!());
            return;
          }
          if (port === "cancel") end();
        },
        // The channel object does not follow the hand, so the host value never changes.
        subscribe: () => () => {}
      };
      useInteraction(`${name}「${options.id()}」`, reorderItemDefinition, view, host);
      onCleanup(() => {
        ids.delete(element);
        if (source === element) end();
        else if (target === element) setTarget(null);
      });
      return (el) => {
        element = el;
        // Focus routes Esc during dragging; -1 keeps the item outside Tab order.
        if (!el.hasAttribute("tabindex")) el.tabIndex = -1;
        ids.set(el, options.id);
      };
    }
  };
}
