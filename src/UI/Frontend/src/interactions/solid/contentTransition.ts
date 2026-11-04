import { createComputed, createEffect, on, untrack } from "solid-js";
import type { Accessor } from "solid-js";
import { enterNode, exitInPlace } from "../../presentation/nodeAnimation.ts";

/* New content becomes interactive immediately. Capture the old content before DOM replacement for a noninteractive exit; fade in the new content independently. */

export type ContentSnapshot = "move" | "clone";

export interface ContentTransitionOptions {
  readonly name: string;
  /** Key of the displayed content. */
  readonly key: Accessor<string>;
  /** Content order, used only to choose the exit direction. */
  readonly order: Accessor<number>;
  /** Element displaying the current content; it may change with the key. */
  readonly stage: () => HTMLElement | undefined;
  /** Element to capture for exit, defaulting to stage. Use the active subregion when stage retains hidden content. */
  readonly leaving?: () => HTMLElement | undefined;
  /** Positioned exit container, defaulting to the stage parent. */
  readonly layer?: () => HTMLElement | undefined;
  /** move retains detached nodes; clone snapshots content replaced in place. */
  readonly snapshot: Accessor<ContentSnapshot>;
  /** Exit direction and distance in pixels; defaults to 8px horizontally. */
  readonly axis?: "x" | "y";
  readonly distance?: number;
}

export function useContentTransition(options: ContentTransitionOptions): void {
  const axis = options.axis ?? "x";
  const distance = options.distance ?? 8;
  let shownOrder = untrack(options.order);

  // Capture old content before the DOM changes.
  createComputed(on(options.key, () => {
    const current = untrack(options.leaving ?? options.stage);
    if (!current) return;
    const next = untrack(options.order);
    const shift = `${next >= shownOrder ? -distance : distance}px`;
    exitInPlace(current, {
      slot: "content",
      keep: untrack(options.snapshot) === "move" ? "children" : "copy",
      layer: options.layer ? untrack(options.layer) : undefined,
      vars: { "--exit-shift-x": axis === "x" ? shift : "0px", "--exit-shift-y": axis === "y" ? shift : "0px" }
    });
  }, { defer: true }));

  // Enter after the new content is mounted.
  createEffect(on(options.key, () => {
    shownOrder = options.order();
    const next = options.stage();
    if (next) enterNode(next, { part: "page", name: options.name });
  }, { defer: true }));
}
