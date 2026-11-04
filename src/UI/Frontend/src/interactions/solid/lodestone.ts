import { createEffect, createSignal, on, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { PartDefinition } from "../components/common.ts";
import type { ParamOverrides } from "../protocol/params.ts";
import { attachDom, expectRelease } from "../protocol/domInput.ts";
import { Runtime } from "../protocol/runtime.ts";
import type { AnimationMode, Composite, Host, Instance, Platform, Presentation } from "../protocol/runtime.ts";
import { Trace } from "../protocol/trace.ts";
import type { Value } from "../protocol/vec.ts";

/* One application runtime receives window capture events and requests frames only while active. Null system parameters use defaults; unsubscribed trace entries are discarded. */

let runtime: Runtime | null = null;
let frameRequested = false;
const [interactionAnimationMode, setAnimationMode] = createSignal<AnimationMode>("normal");
export { interactionAnimationMode };

type Rect = { left: number; top: number; right: number; bottom: number };

/** Register visual component bounds when they differ from the element bounds, such as a native range thumb. Registrations follow element lifetime. */
const componentBoxes = new WeakMap<Element, () => Rect>();

export function setInteractionComponentBox(element: Element, box: () => Rect): void {
  componentBoxes.set(element, box);
}

const platform: Platform = {
  contains: (element, target) => element instanceof Node && target instanceof Node && element.contains(target),
  rect: (element) => {
    if (!(element instanceof Element)) return null;
    const box = componentBoxes.get(element);
    if (box) return box();
    const r = element.getBoundingClientRect();
    return { left: r.left, top: r.top, right: r.right, bottom: r.bottom };
  },
  // Pointer capture can fail after the pointer disappears; subsequent input corrects the state.
  capture: (element, pointerId) => { try { (element as HTMLElement).setPointerCapture(pointerId); } catch {  } },
  release: (element, pointerId) => {
    const target = element as HTMLElement;
    if (!target.hasPointerCapture(pointerId)) return;
    expectRelease(pointerId);
    target.releasePointerCapture(pointerId);
  },
  focus: (element) => (element as HTMLElement).focus({ preventScroll: true }),
  responseZone: () => ({ left: 0, top: 0, right: window.innerWidth, bottom: window.innerHeight }),
  requestFrame: () => {
    if (frameRequested) return;
    frameRequested = true;
    requestAnimationFrame(() => {
      frameRequested = false;
      interactionRuntime().frame(performance.now());
    });
  }
};

export function interactionRuntime(): Runtime {
  if (runtime === null) {
    const created = new Runtime(null, new Trace(), platform, { mode: "optimized" });
    // The input adapter suppresses clicks following claimed releases.
    attachDom(window, (raw) => created.feed(raw));
    runtime = created;
  }
  return runtime;
}

/** Engine mode published by the active animation style. */
export function setInteractionAnimationMode(mode: AnimationMode): void {
  setAnimationMode(mode);
  interactionRuntime().setAnimationMode(mode, performance.now());
}

/** Replace the global parameter layer atomically; definition-level values remain authoritative. */
export function setInteractionGlobalOverrides(overrides: Readonly<ParamOverrides>): void {
  interactionRuntime().setOverrides(null, { ...overrides }, performance.now());
}

/** Mounted instance, or null after unmount; used by group routing. */
export interface InteractionHandle<V extends Value = Value> {
  readonly instance: Instance<V> | null;
}

/** Mount a plane-motion definition, view, and host. Create the view after the DOM exists; remove the instance on cleanup. */
export function useInteraction<V extends Value>(name: string, definition: PartDefinition<V, "平面移动">, view: () => Presentation<V>, host: Host<V>): InteractionHandle<V> {
  const handle: { instance: Instance<V> | null } = { instance: null };
  onMount(() => {
    const instance = interactionRuntime().add(name, definition, view(), host);
    handle.instance = instance;
    onCleanup(() => {
      handle.instance = null;
      interactionRuntime().remove(instance, performance.now());
    });
  });
  return handle;
}

/** Mount a group with live child routing. Routes receive viewport coordinates and return a child and reason, or null. */
export function useInteractionComposite(
  name: string,
  element: () => Element,
  children: () => readonly Instance[],
  route: (position: { x: number; y: number }) => { child: Instance; why: string } | null
): void {
  onMount(() => {
    const composite: Composite = {
      name,
      element: element(),
      get children() { return children(); },
      overrides: {},
      route
    };
    interactionRuntime().addComposite(composite);
    onCleanup(() => interactionRuntime().remove(composite, performance.now()));
  });
}

/** Adapt Solid accessors to a host. Notify value changes without echoing business calls. */
export function solidHost<V extends Value>(options: {
  value: Accessor<V>;
  enabled: Accessor<boolean>;
  call: (port: string, args: readonly unknown[]) => void;
}): Host<V> {
  const listeners = new Set<(old: V, next: V, cause: string) => void>();
  createEffect(on(options.value, (next, old) => {
    if (old === undefined) return;
    for (const listener of listeners) listener(old, next, "宿主值变化");
  }));
  return {
    value: options.value,
    enabled: options.enabled,
    call: (port, args) => options.call(port, args),
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    }
  };
}
