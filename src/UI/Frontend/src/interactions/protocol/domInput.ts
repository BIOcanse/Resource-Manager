// Adapt window capture events into raw observations; components attach no listeners.

import type { Modifiers, RawInput } from "./input.ts";

const mods = (e: MouseEvent | KeyboardEvent | WheelEvent): Modifiers => ({ shift: e.shiftKey, ctrl: e.ctrlKey, alt: e.altKey, meta: e.metaKey });

/** Record intentional capture release so its lostpointercapture is not treated as input loss. */
const expectedReleases = new Set<number>();
export function expectRelease(pointerId: number): void {
  expectedReleases.add(pointerId);
}

export function attachDom(win: Window, feed: (raw: RawInput) => void): () => void {
  let buttons = 0;
  /** Suppress the click following a claimed release. Clear suppression on the next press if no click arrives. */
  let swallowClick = false;
  // Deliver each browser event once; retain coalesced samples as history without extra position changes.
  const pointer = (e: PointerEvent) => {
    if (!e.isPrimary) return;
    if (e.type === "pointerdown") swallowClick = false;
    const coalesced = e.type === "pointermove" && typeof e.getCoalescedEvents === "function" ? e.getCoalescedEvents() : [];
    const list = coalesced.length ? coalesced : [e];
    const last = list[list.length - 1];
    buttons = e.buttons;
    feed({ type: "pointer", t: last.timeStamp, x: last.clientX, y: last.clientY, buttons: e.buttons, pointerId: e.pointerId,
      samples: list.map((s) => ({ t: s.timeStamp, x: s.clientX, y: s.clientY })),
      dom: e.type + (list.length > 1 ? `（合并采样 ${list.length} 个）` : ""), modifiers: mods(e), target: e.target, event: e });
    if (e.type === "pointerup" && e.defaultPrevented) swallowClick = true;
  };
  const handlers: [EventTarget, string, (e: Event) => void][] = [
    [win, "pointerdown", (e) => pointer(e as PointerEvent)],
    [win, "pointermove", (e) => pointer(e as PointerEvent)],
    [win, "pointerup", (e) => pointer(e as PointerEvent)],
    // pointerout with no related target detects viewport exit even when pointerleave is absent.
    [win, "pointerout", (e) => {
      const p = e as PointerEvent;
      if (p.isPrimary && p.relatedTarget === null) feed({ type: "pointerout", t: p.timeStamp, target: null, event: p });
    }],
    [win, "pointercancel", (e) => feed({ type: "loss", t: e.timeStamp, id: "loss.pointercancel", target: e.target, event: e })],
    // Only unrequested capture loss while a button is held counts as input loss.
    [win, "lostpointercapture", (e) => {
      const id = (e as PointerEvent).pointerId;
      if (expectedReleases.delete(id)) return;
      if (buttons !== 0) feed({ type: "loss", t: e.timeStamp, id: "loss.lostcapture", target: e.target, event: e });
    }],
    [win, "wheel", (e) => {
      const w = e as WheelEvent;
      feed({ type: "wheel", t: w.timeStamp, x: w.clientX, y: w.clientY, dx: w.deltaX, dy: w.deltaY, deltaMode: w.deltaMode, modifiers: mods(w), target: w.target, event: w });
    }],
    [win, "keydown", (e) => {
      const k = e as KeyboardEvent;
      feed({ type: "key", t: k.timeStamp, code: k.code, key: k.key, down: true, repeat: k.repeat, modifiers: mods(k), target: k.target, event: k });
    }],
    [win, "keyup", (e) => {
      const k = e as KeyboardEvent;
      feed({ type: "key", t: k.timeStamp, code: k.code, key: k.key, down: false, repeat: false, modifiers: mods(k), target: k.target, event: k });
    }],
    // Pointer clicks were handled by levels; detail=0 represents keyboard, assistive, or scripted activation.
    [win, "click", (e) => {
      if ((e as MouseEvent).detail === 0) { feed({ type: "activate", t: e.timeStamp, target: e.target, event: e }); return; }
      if (!swallowClick) return;
      swallowClick = false;
      e.preventDefault();
      e.stopImmediatePropagation();
    }],
    [win, "contextmenu", (e) => {
      const m = e as MouseEvent;
      feed({ type: "contextmenu", t: m.timeStamp, x: m.clientX, y: m.clientY, target: m.target, event: m });
    }],
    [win, "blur", (e) => { if (e.target === win) feed({ type: "loss", t: e.timeStamp, id: "loss.blur", target: null, event: e }); }],
    [win, "focus", (e) => { if (e.target === win) feed({ type: "loss", t: e.timeStamp, id: "loss.focus", target: null, event: e }); }],
    [win.document, "visibilitychange", (e) => feed({ type: "loss", t: e.timeStamp, id: win.document.hidden ? "loss.hidden" : "loss.visible", target: null, event: e })],
  ];
  for (const [target, type, fn] of handlers) target.addEventListener(type, fn, { capture: true, passive: false });
  return () => {
    for (const [target, type, fn] of handlers) target.removeEventListener(type, fn, { capture: true });
  };
}
