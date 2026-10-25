// DOM 适配：在 window 捕获阶段收浏览器事件，转成输入层的原始观测。组件自己不挂任何监听。

import type { Modifiers, RawInput } from "./input.ts";

const mods = (e: MouseEvent | KeyboardEvent | WheelEvent): Modifiers => ({ shift: e.shiftKey, ctrl: e.ctrlKey, alt: e.altKey, meta: e.metaKey });

/** 组件主动释放捕获（框架输出“释放捕获”）前登记，随后到来的 lostpointercapture 不算输入丢失。 */
const expectedReleases = new Set<number>();
export function expectRelease(pointerId: number): void {
  expectedReleases.add(pointerId);
}

export function attachDom(win: Window, feed: (raw: RawInput) => void): () => void {
  let buttons = 0;
  // 一个浏览器事件送一次：浏览器按帧合并的采样（getCoalescedEvents）全部带上，进输入层的历史；位置变化只算一次。
  const pointer = (e: PointerEvent) => {
    if (!e.isPrimary) return;
    const coalesced = e.type === "pointermove" && typeof e.getCoalescedEvents === "function" ? e.getCoalescedEvents() : [];
    const list = coalesced.length ? coalesced : [e];
    const last = list[list.length - 1];
    buttons = e.buttons;
    feed({ type: "pointer", t: last.timeStamp, x: last.clientX, y: last.clientY, buttons: e.buttons, pointerId: e.pointerId,
      samples: list.map((s) => ({ t: s.timeStamp, x: s.clientX, y: s.clientY })),
      dom: e.type + (list.length > 1 ? `（合并采样 ${list.length} 个）` : ""), modifiers: mods(e), target: e.target, event: e });
  };
  const handlers: [EventTarget, string, (e: Event) => void][] = [
    [win, "pointerdown", (e) => pointer(e as PointerEvent)],
    [win, "pointermove", (e) => pointer(e as PointerEvent)],
    [win, "pointerup", (e) => pointer(e as PointerEvent)],
    // 指针移到了窗口外：relatedTarget 为空的 pointerout（Edge 实测移出视口时只发它，不发 pointerleave）
    [win, "pointerout", (e) => {
      const p = e as PointerEvent;
      if (p.isPrimary && p.relatedTarget === null) feed({ type: "pointerout", t: p.timeStamp, target: null, event: p });
    }],
    [win, "pointercancel", (e) => feed({ type: "loss", t: e.timeStamp, id: "loss.pointercancel", target: e.target, event: e })],
    // 主动释放和松开后浏览器自动释放也会触发它；只有键还按着、又不是我们主动释放的，才算意外失去。
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
    // 指针产生的 click 的 detail ≥ 1，已经由电平处理；detail = 0 的是键盘、辅助技术或脚本发出的激活请求。
    [win, "click", (e) => { if ((e as MouseEvent).detail === 0) feed({ type: "activate", t: e.timeStamp, target: e.target, event: e }); }],
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
