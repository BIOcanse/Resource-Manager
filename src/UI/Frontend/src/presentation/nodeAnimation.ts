import { onCleanup, untrack } from "solid-js";
import { entranceDefinitions } from "../interactions/components/entrance.ts";
import type { EntrancePart } from "../interactions/components/entrance.ts";
import type { MotionKind } from "../interactions/components/common.ts";
import { reflowDefinition } from "../interactions/components/reflow.ts";
import type { Vec } from "../interactions/protocol/vec.ts";
import type { Instance, Presentation } from "../interactions/protocol/runtime.ts";
import { interactionAnimationMode, interactionRuntime } from "../interactions/solid/lodestone.ts";
import { createOrderHost } from "../interactions/solid/orderHost.ts";

/* DOM state, focus, input routing, and hit geometry reach their final state immediately. This layer alone owns visual entrance, exit, and reflow state. Exit ghosts are inert, hidden from accessibility, stripped of identity, absolutely positioned, and removed after animation. Entrances change opacity only; transforms would move hit regions. Reflow hides the real node at its interactive final geometry while a visual copy moves there. Disabled modes settle immediately. */

/** Entrance slots affect opacity on real nodes; exit slots animate ghosts. Unspecified exits use class-based CSS defaults. Lodestone entrance definitions declare the same motion category. */
type EntranceOrExit = Extract<MotionKind, "进场退场">;
export const ENTER_SLOTS = { row: "进场退场", disclosure: "进场退场" } as const satisfies Readonly<Record<string, EntranceOrExit>>;
export const EXIT_SLOTS = { content: "进场退场", disclosure: "进场退场" } as const satisfies Readonly<Record<string, EntranceOrExit>>;
export type EnterSlot = keyof typeof ENTER_SLOTS;
export type ExitSlot = keyof typeof EXIT_SLOTS;

/** Maximum nodes cloned or sanitized per ghost; exceeding it skips the visual exit. */
const GHOST_NODE_LIMIT = 20000;
/** Maximum ghost lifetime in ms, even when slot completion events are lost. */
const GHOST_LIFETIME_MS = 3000;
/** Wait in ms for animationstart before treating the slot as empty. */
const SLOT_START_WAIT_MS = 250;
/** Maximum ancestor depth searched for a connected placement parent. */
const ANCESTOR_LIMIT = 64;
/** Keep only data attributes used for styling. Remove identity, field, focus, and viewport attributes so ghosts cannot match real-node queries. */
export const GHOST_STYLE_DATA_ATTRIBUTES: ReadonlySet<string> = new Set([
  "data-column-id", "data-tone", "data-bar-color", "data-placement", "data-form-factor", "data-exit-slot", "data-outcome", "data-active", "data-state"
]);
const GHOST_REMOVED_ATTRIBUTES = ["id", "role", "tabindex", "name", "for", "autofocus", "contenteditable", "href"];

/** Disabled and ultra modes have no animation slots. */
function slotsAreEmpty(): boolean {
  return untrack(interactionAnimationMode) !== "normal";
}

export interface ExitOptions {
  /** Ghost data-exit-slot name; absent uses the node class default. */
  readonly slot?: ExitSlot;
  /** CSS variables required by the exit slot, such as direction. */
  readonly vars?: Readonly<Record<string, string>>;
  /** Positioning container in whose coordinates the ghost is placed; defaults to the original parent. Use a stable layer if that parent will scroll or move. */
  readonly layer?: HTMLElement;
}

interface Placement {
  readonly parents: readonly HTMLElement[];
  readonly box: { left: number; top: number; width: number; height: number } | null;
  readonly scrolls: readonly { path: readonly number[]; left: number; top: number }[];
}

/** Capture nearest-first ancestors, optional absolute box, and descendant scroll positions. Disconnected, hidden, or boxless nodes return null. */
function measure(element: HTMLElement, layer?: HTMLElement): Placement | null {
  if (!element.isConnected || !element.parentElement || !element.checkVisibility()) return null;
  const style = getComputedStyle(element);
  if (layer) {
    const rect = element.getBoundingClientRect();
    const origin = layer.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return null;
    return {
      parents: [layer],
      box: { left: rect.left - origin.left - layer.clientLeft + layer.scrollLeft, top: rect.top - origin.top - layer.clientTop + layer.scrollTop, width: rect.width, height: rect.height },
      scrolls: scrolledInside(element)
    };
  }
  const parents: HTMLElement[] = [];
  let ancestor: HTMLElement | null = element.parentElement;
  while (ancestor && parents.length < ANCESTOR_LIMIT) {
    parents.push(ancestor);
    ancestor = ancestor.parentElement;
  }
  let box: Placement["box"] = null;
  if (style.position !== "absolute" && style.position !== "fixed") {
    const rect = element.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return null;
    const base = element.offsetParent instanceof HTMLElement && element.offsetParent !== document.body ? element.offsetParent : null;
    const origin = base ? base.getBoundingClientRect() : { left: -window.scrollX, top: -window.scrollY };
    box = {
      left: rect.left - origin.left - (base?.clientLeft ?? 0) + (base?.scrollLeft ?? 0),
      top: rect.top - origin.top - (base?.clientTop ?? 0) + (base?.scrollTop ?? 0),
      width: rect.width,
      height: rect.height
    };
  }
  return { parents, box, scrolls: scrolledInside(element) };
}

/** Record scroll positions through a passive capture listener, avoiding per-descendant layout reads during exit. Drop zero and disconnected entries. */
const scrollPositions = new Map<Element, { readonly left: number; readonly top: number }>();
if (typeof document !== "undefined") {
  document.addEventListener("scroll", (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    if (target.scrollTop === 0 && target.scrollLeft === 0) scrollPositions.delete(target);
    else scrollPositions.set(target, { left: target.scrollLeft, top: target.scrollTop });
  }, { capture: true, passive: true });
}

/** Remember descendant scroll positions by child-index path, then restore after moving or cloning. */
function scrolledInside(root: HTMLElement): { path: number[]; left: number; top: number }[] {
  const found: { path: number[]; left: number; top: number }[] = [];
  for (const [element, at] of scrollPositions) {
    if (!element.isConnected) {
      scrollPositions.delete(element);
      continue;
    }
    if (!root.contains(element)) continue;
    const path: number[] = [];
    let node: Element = element;
    for (let depth = 0; node !== root && node.parentElement && depth < ANCESTOR_LIMIT; depth += 1) {
      path.unshift(Array.prototype.indexOf.call(node.parentElement.children, node));
      node = node.parentElement;
    }
    if (node === root) found.push({ path, left: at.left, top: at.top });
  }
  return found;
}

function atPath(root: Element, path: readonly number[]): Element | null {
  let element: Element | null = root;
  for (const index of path) {
    element = element?.children[index] ?? null;
  }
  return element;
}

/** Strip identity and interaction attributes with a bounded iterative walk; false if the node limit is exceeded. */
function stripIdentity(ghost: HTMLElement): boolean {
  const walker = document.createTreeWalker(ghost, NodeFilter.SHOW_ELEMENT);
  let visited = 0;
  for (let node: Node | null = ghost; node; node = walker.nextNode()) {
    visited += 1;
    if (visited > GHOST_NODE_LIMIT) return false;
    const element = node as Element;
    for (const name of GHOST_REMOVED_ATTRIBUTES) element.removeAttribute(name);
    const names = element.getAttributeNames();
    for (const name of names) {
      if ((name.startsWith("aria-") && name !== "aria-hidden") || (name.startsWith("data-") && !GHOST_STYLE_DATA_ATTRIBUTES.has(name))) {
        element.removeAttribute(name);
      }
    }
  }
  return true;
}

/** Copy canvas pixels and live form values omitted by cloneNode by walking matching tree structures. */
function copyLook(source: HTMLElement, copy: HTMLElement): void {
  const from = document.createTreeWalker(source, NodeFilter.SHOW_ELEMENT);
  const to = document.createTreeWalker(copy, NodeFilter.SHOW_ELEMENT);
  let visited = 0;
  for (let a: Node | null = source, b: Node | null = copy; a && b && visited < GHOST_NODE_LIMIT; a = from.nextNode(), b = to.nextNode()) {
    visited += 1;
    if (a instanceof HTMLCanvasElement && b instanceof HTMLCanvasElement && a.width > 0 && a.height > 0) {
      b.getContext("2d")?.drawImage(a, 0, 0);
    } else if ((a instanceof HTMLInputElement && b instanceof HTMLInputElement)) {
      b.value = a.value;
      b.checked = a.checked;
    } else if (a instanceof HTMLTextAreaElement && b instanceof HTMLTextAreaElement) {
      b.value = a.value;
    } else if (a instanceof HTMLSelectElement && b instanceof HTMLSelectElement) {
      b.selectedIndex = a.selectedIndex;
    }
  }
}

/** Place the ghost in its original parent or a connected ancestor, animate, then remove it. Defer tree-wide work to the next animation frame so input completion stays immediate. Hidden documents skip ghosts. */
function place(ghost: HTMLElement, placement: Placement, options: ExitOptions): void {
  if (document.visibilityState === "hidden") return;
  requestAnimationFrame(() => {
    // The node was reattached before this frame and remains in use; skip the exit.
    if (ghost.isConnected) return;
    const parent = placement.parents.find((element) => element.isConnected);
    if (!parent || !stripIdentity(ghost)) return;
    ghost.setAttribute("data-exit-ghost", "");
    if (options.slot) ghost.setAttribute("data-exit-slot", options.slot);
    ghost.setAttribute("aria-hidden", "true");
    ghost.inert = true;
    ghost.style.pointerEvents = "none";
    if (placement.box) {
      Object.assign(ghost.style, {
        position: "absolute",
        left: `${placement.box.left}px`,
        top: `${placement.box.top}px`,
        width: `${placement.box.width}px`,
        height: `${placement.box.height}px`,
        margin: "0",
        boxSizing: "border-box"
      });
    }
    for (const [name, value] of Object.entries(options.vars ?? {})) ghost.style.setProperty(name, value);
    parent.append(ghost);
    for (const scroll of placement.scrolls) {
      const element = atPath(ghost, scroll.path);
      if (element) {
        element.scrollLeft = scroll.left;
        element.scrollTop = scroll.top;
      }
    }
    // Wait only for exit-slot animations, excluding copied entrances and spinners; empty slots expire after SLOT_START_WAIT_MS.
    awaitSlotAnimations(ghost, EXIT_KEYFRAMES, () => ghost.remove());
  });
}

/** Exit-slot keyframes used by CSS ghost rules. */
export const EXIT_KEYFRAMES: ReadonlySet<string> = new Set(["motion-fade-out", "motion-sink-out", "motion-dialog-out", "motion-toast-out", "motion-content-out", "motion-row-out"]);
/** Entrance-slot keyframes used by CSS enter rules. */
export const ENTER_KEYFRAMES: ReadonlySet<string> = new Set(["motion-fade-in"]);

/** Count matching bubbled animationstart/end/cancel events rather than getAnimations, which would recalculate subtree styles. No start by SLOT_START_WAIT_MS means empty; GHOST_LIFETIME_MS bounds the wait. */
function awaitSlotAnimations(root: HTMLElement, keyframes: ReadonlySet<string>, done: () => void): void {
  let active = 0;
  let started = false;
  let finished = false;
  const onStart = (event: AnimationEvent) => {
    if (!keyframes.has(event.animationName)) return;
    started = true;
    active += 1;
  };
  const onEnd = (event: AnimationEvent) => {
    if (!keyframes.has(event.animationName)) return;
    active -= 1;
    if (active <= 0) finish();
  };
  const timer = window.setTimeout(() => finish(), GHOST_LIFETIME_MS);
  function finish(): void {
    if (finished) return;
    finished = true;
    window.clearTimeout(timer);
    root.removeEventListener("animationstart", onStart);
    root.removeEventListener("animationend", onEnd);
    root.removeEventListener("animationcancel", onEnd);
    done();
  }
  root.addEventListener("animationstart", onStart);
  root.addEventListener("animationend", onEnd);
  root.addEventListener("animationcancel", onEnd);
  window.setTimeout(() => {
    if (!started) finish();
  }, SLOT_START_WAIT_MS);
}

/** Blocks exiting in this task, cleared in a microtask; descendant removals share their ghost. */
const leavingRoots = new Set<HTMLElement>();

/** Register while mounted. On cleanup, measure before removal; after removal, reuse the node as its exit ghost to retain canvas and scroll state. */
export function exitOnUnmount(element: HTMLElement, options: ExitOptions = {}): void {
  onCleanup(() => {
    if (slotsAreEmpty()) return;
    // A containing block already owns the exit ghost; skip extra measurement and ghosts.
    for (const root of leavingRoots) if (root.contains(element)) return;
    // Read geometry and scroll state before stopping entrances, whose style writes would force layout.
    const placement = measure(element, options.layer);
    const stopped = stopEntrancesWithin(element);
    queueMicrotask(() => {
      // Cleanup did not remove the node; skip the exit.
      if (placement && !element.isConnected) place(element, placement, options);
      settleStopped(stopped);
    });
  });
}

/** Before replacing content, clone its current appearance as an exit ghost. With keep=children, clone only the shell and move detached children into it. */
export function exitInPlace(element: HTMLElement, options: ExitOptions & { readonly keep?: "copy" | "children" } = {}): void {
  if (slotsAreEmpty()) return;
  const placement = measure(element, options.layer);
  const stopped = stopEntrancesWithin(element);
  if (!placement) {
    queueMicrotask(() => settleStopped(stopped));
    return;
  }
  leavingRoots.add(element);
  queueMicrotask(() => leavingRoots.delete(element));
  if (options.keep === "children") {
    const shell = element.cloneNode(false) as HTMLElement;
    const nodes = [...element.childNodes];
    queueMicrotask(() => {
      shell.append(...nodes.filter((node) => !node.isConnected));
      if (shell.childNodes.length > 0) place(shell, placement, options);
      settleStopped(stopped);
    });
    return;
  }
  const copy = element.cloneNode(true) as HTMLElement;
  copyLook(element, copy);
  place(copy, placement, options);
  settleStopped(stopped);
}

// Lodestone entrances.

interface Entrance {
  readonly element: HTMLElement;
  readonly instance: Instance;
  readonly faded: readonly HTMLElement[];
}

/** Active entrance state is owned only by this layer. */
const entrances = new Map<HTMLElement, Entrance>();

function clearLook(elements: readonly HTMLElement[]): void {
  for (const element of elements) {
    element.style.removeProperty("opacity");
    element.style.removeProperty("will-change");
  }
}

function finishEntrance(entrance: Entrance): void {
  if (entrances.get(entrance.element) !== entrance) return;
  entrances.delete(entrance.element);
  clearLook(entrance.faded);
  interactionRuntime().remove(entrance.instance, performance.now());
}

/** Stop active entrances on the node and descendants. Retain their current opacity for the ghost, then settle via settleStopped after placement. */
function stopEntrancesWithin(root: HTMLElement): readonly Entrance[] {
  const stopped: Entrance[] = [];
  for (const entrance of [...entrances.values()]) {
    if (root.contains(entrance.element)) {
      entrances.delete(entrance.element);
      interactionRuntime().remove(entrance.instance, performance.now());
      stopped.push(entrance);
    }
  }
  return stopped;
}

/** Clear stopped entrance opacity on nodes still on the page, including hidden pages and nodes behind copied ghosts. Leave newer entrance state untouched. */
function settleStopped(stopped: readonly Entrance[]): void {
  for (const entrance of stopped) {
    if (entrances.has(entrance.element)) continue;
    if (entrance.element.isConnected && !entrance.element.closest("[data-exit-ghost]")) clearLook(entrance.faded);
  }
}

/** Fade the mounted node and alsoFade elements from zero at final interactive geometry. Reentry starts a fresh stationary part; disabled modes settle immediately. */
export function enterNode(element: HTMLElement, options: { readonly part: EntrancePart; readonly name: string; readonly alsoFade?: readonly HTMLElement[] }): void {
  const running = entrances.get(element);
  if (running) finishEntrance(running);
  if (slotsAreEmpty()) return;
  const faded = [element, ...(options.alsoFade ?? [])];
  const host = createOrderHost(0);
  let entrance: Entrance | null = null;
  const draw = (x: number) => {
    if (!entrance || entrances.get(element) !== entrance) return;
    // Disconnected nodes no longer draw.
    if (!element.isConnected) {
      const done = entrance;
      queueMicrotask(() => finishEntrance(done));
      return;
    }
    const p = Math.min(1, Math.max(0, x));
    if (p >= 1) {
      const done = entrance;
      clearLook(faded);
      queueMicrotask(() => finishEntrance(done));
      return;
    }
    for (const target of faded) {
      target.style.willChange = "opacity";
      target.style.opacity = p.toFixed(4);
    }
  };
  const view: Presentation = {
    element,
    bindingLabel: "不透明度",
    geometry: () => null,
    render: (d) => draw(d.x as number),
    aria: () => ""
  };
  const instance = interactionRuntime().add(options.name, entranceDefinitions[options.part], view, host.host);
  entrance = { element, instance, faded };
  entrances.set(element, entrance);
  draw(0);
  host.set(1, "节点进场");
}

// Reflow.

interface Reflow {
  readonly element: HTMLElement;
  readonly instance: Instance;
  /** Original inline opacity, restored when reflow ends. */
  readonly opacity: string;
  readonly timer: number;
  copy: HTMLElement | null;
  /** Current copy value in caller coordinates; interrupted reflow starts here. */
  drawn: Vec;
}

/** Active reflow state is owned only by this layer. */
const reflows = new Map<HTMLElement, Reflow>();

function finishReflow(reflow: Reflow): void {
  if (reflows.get(reflow.element) !== reflow) return;
  reflows.delete(reflow.element);
  window.clearTimeout(reflow.timer);
  reflow.copy?.remove();
  if (reflow.opacity) reflow.element.style.opacity = reflow.opacity;
  else reflow.element.style.removeProperty("opacity");
  interactionRuntime().remove(reflow.instance, performance.now());
}

/** Draw the real node at its final interactive geometry, then animate a visual copy from the current appearance. Each reflow starts a stationary part. Disabled, hidden, or unchanged states draw only the real node. */
export function reflowNode(element: HTMLElement, options: { readonly name: string; readonly from: Vec; readonly to: Vec; readonly draw: (target: HTMLElement, at: Vec) => void }): void {
  const running = reflows.get(element);
  const from = running ? running.drawn : options.from;
  if (running) finishReflow(running);
  const to = options.to;
  options.draw(element, to);
  if (slotsAreEmpty() || document.visibilityState === "hidden" || !element.isConnected || (from.x === to.x && from.y === to.y)) return;
  const opacity = element.style.opacity;
  element.style.opacity = "0";
  const host = createOrderHost(0);
  let reflow: Reflow | null = null;
  const render = (x: number) => {
    if (!reflow || reflows.get(element) !== reflow) return;
    const done = reflow;
    if (!element.isConnected) {
      queueMicrotask(() => finishReflow(done));
      return;
    }
    const p = Math.min(1, Math.max(0, x));
    done.drawn = { x: from.x + (to.x - from.x) * p, y: from.y + (to.y - from.y) * p };
    if (done.copy) options.draw(done.copy, done.drawn);
    if (p >= 1) queueMicrotask(() => finishReflow(done));
  };
  const view: Presentation = {
    element,
    bindingLabel: "副本的位置和大小",
    geometry: () => null,
    render: (d) => render(d.x as number),
    aria: () => ""
  };
  const instance = interactionRuntime().add(options.name, reflowDefinition, view, host.host);
  const started: Reflow = { element, instance, opacity, timer: 0, copy: null, drawn: from };
  reflow = { ...started, timer: window.setTimeout(() => reflow && finishReflow(reflow), GHOST_LIFETIME_MS) };
  reflows.set(element, reflow);
  // Defer cloning and identity stripping to the next frame; the real node is already final and visually hidden.
  requestAnimationFrame(() => {
    const current = reflow;
    if (!current || reflows.get(element) !== current || !element.isConnected) return;
    const copy = element.cloneNode(true) as HTMLElement;
    copyLook(element, copy);
    if (!stripIdentity(copy)) {
      finishReflow(current);
      return;
    }
    copy.setAttribute("data-exit-ghost", "");
    copy.setAttribute("data-reflow-copy", "");
    copy.setAttribute("aria-hidden", "true");
    copy.inert = true;
    copy.style.pointerEvents = "none";
    if (opacity) copy.style.opacity = opacity;
    else copy.style.removeProperty("opacity");
    options.draw(copy, current.drawn);
    element.after(copy);
    current.copy = copy;
  });
  host.set(1, "重排");
}

// CSS entrances.

/** Run the opacity-only CSS entrance slot and remove its attribute afterward. Disabled modes settle immediately without adding the attribute. */
export function enterSlot(element: HTMLElement, slot: EnterSlot): void {
  if (slotsAreEmpty()) return;
  element.setAttribute("data-enter-slot", slot);
  awaitSlotAnimations(element, ENTER_KEYFRAMES, () => {
    if (element.getAttribute("data-enter-slot") === slot) element.removeAttribute("data-enter-slot");
  });
}

/** Finish active CSS entrances within root, such as rows reused by a virtual table. */
export function settleEntrancesWithin(root: HTMLElement): void {
  for (const element of root.querySelectorAll("[data-enter-slot]")) element.removeAttribute("data-enter-slot");
}
