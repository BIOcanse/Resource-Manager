// 控制面数值滑条的定义（src/interactions/components/controlSlider.ts）：按下不改值，离开拖动框起跟着指针预览、松开写一次；单击照原生 range；
// Esc、右键取消（只调用 cancel，不写）；取消后的松开、移动什么都不调用；方向键、PageUp / PageDown 调用 step，Home / End 停在两端。
import assert from "node:assert/strict";
import { controlSliderDefinition } from "../src/interactions/components/controlSlider.ts";
import type { RawInput } from "../src/interactions/protocol/input.ts";
import { Runtime } from "../src/interactions/protocol/runtime.ts";
import type { Host, Platform, Presentation } from "../src/interactions/protocol/runtime.ts";
import { Trace } from "../src/interactions/protocol/trace.ts";

const NO_MODS = { shift: false, ctrl: false, alt: false, meta: false };
const SYSTEM = {
  doubleClickTimeMs: 500, doubleClickWidthPx: 4, doubleClickHeightPx: 4, dragWidthPx: 4, dragHeightPx: 4,
  keyboardDelayStep: 1, keyboardSpeedStep: 31, wheelScrollLines: 3,
};
const el = {};
const platform: Platform = {
  contains: (a, b) => a === b,
  rect: () => ({ left: 0, top: 0, right: 400, bottom: 16 }),
  capture: () => {}, release: () => {}, focus: () => {},
  responseZone: () => ({ left: -10000, top: -10000, right: 10000, bottom: 10000 }),
  requestFrame: () => {},
};
// 滑条宽 400 px：比例 0 时滑块圆心在 8 px，比例 1 时在 392 px。宿主值 0.4。
let hostValue = 0.4;
const listeners = new Set<(old: number, next: number, cause: string) => void>();
const calls: string[] = [];
const host: Host<number> = {
  value: () => hostValue, enabled: () => true,
  call: (port, args) => {
    calls.push(`${port}(${args.map((a) => (typeof a === "number" ? a.toFixed(3) : JSON.stringify(a))).join(", ")})`);
    if (port === "select" && args[1] === true) setHost(Number(args[0]));
  },
  subscribe: (listener) => { listeners.add(listener); return () => listeners.delete(listener); },
};
const setHost = (next: number) => { const old = hostValue; hostValue = next; for (const l of listeners) l(old, next, "宿主值变化"); };
const xOf = (ratio: number) => 8 + ratio * 384;
const geometry = { center: "滑块圆心", atMin: { x: 8, y: 8 }, atMax: { x: 392, y: 8 } };
const view: Presentation = { element: el, bindingLabel: "滑块位置", geometry: () => geometry, render() {}, aria: () => "" };
const rt = new Runtime(SYSTEM, new Trace(), platform, { mode: "optimized" });
const inst = rt.add("数值滑条", controlSliderDefinition, view, host);

let t = 0;
const ptr = (x: number, buttons: number, dom = "pointermove") => {
  t += 10;
  const event = { defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
  rt.feed({ type: "pointer", t, x, y: 8, buttons, pointerId: 1, dom, modifiers: NO_MODS, target: el, event } satisfies RawInput);
  return event.defaultPrevented;
};
const key = (code: string) => {
  t += 10;
  const event = { defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
  rt.feed({ type: "key", t, code, key: code, down: true, repeat: false, modifiers: NO_MODS, target: el, event } satisfies RawInput);
  t += 10;
  rt.feed({ type: "key", t, code, key: code, down: false, repeat: false, modifiers: NO_MODS, target: el, event: {} } satisfies RawInput);
  return event.defaultPrevented;
};
const settle = () => { for (let i = 0; i < 60; i += 1) { t += 16; rt.frame(t); } };

// 拖动：按下不改值；离开拖动框起滑块圆心在指针下，连续预览；松开写一次。
ptr(xOf(0.1), 0);
assert.ok(ptr(xOf(0.1), 1, "pointerdown"), "按下被认领（原生 range 不自己拖）");
assert.deepEqual(calls, [], "按下不改值");
ptr(xOf(0.3), 1);
ptr(xOf(0.65), 1);
ptr(xOf(0.65), 0, "pointerup");
assert.deepEqual(calls, ["select(0.300, false)", "select(0.650, false)", "select(0.650, true)"]);
assert.equal(inst.mode, "静止");
settle();

// 单击：点在滑块上（离圆心不超过 8 px）停在宿主值，宿主不写；点在滑轨上到那里。
calls.length = 0;
ptr(xOf(0.65) + 6, 1, "pointerdown");
ptr(xOf(0.65) + 6, 0, "pointerup");
ptr(xOf(0.2), 1, "pointerdown");
ptr(xOf(0.2), 0, "pointerup");
assert.deepEqual(calls, ["select(0.650, true)", "select(0.200, true)"]);
settle();
assert.ok(Math.abs(inst.ch.x - 0.2) < 1e-6, "单击滑轨后滑块到那里");

// Esc 取消：只调用 cancel，之后的移动和松开什么都不调用，回到拖动前的值。
calls.length = 0;
ptr(xOf(0.2), 1, "pointerdown");
ptr(xOf(0.5), 1);
assert.ok(key("Escape"), "Esc 被认领");
ptr(xOf(0.7), 1);
ptr(xOf(0.7), 0, "pointerup");
assert.deepEqual(calls, ["select(0.500, false)", "cancel()"], "Esc 只取消，不写");
settle();
assert.ok(Math.abs(inst.ch.x - 0.2) < 1e-6, "Esc 后回到拖动前的值");

// 右键取消。
calls.length = 0;
ptr(xOf(0.2), 1, "pointerdown");
ptr(xOf(0.9), 1);
ptr(xOf(0.9), 3, "pointerdown");
ptr(xOf(0.9), 1, "pointerup");
ptr(xOf(0.9), 0, "pointerup");
assert.deepEqual(calls, ["select(0.900, false)", "cancel()"], "右键只取消，不写");
settle();
assert.ok(Math.abs(inst.ch.x - 0.2) < 1e-6, "右键后回到拖动前的值");

// 键盘：方向键、PageUp / PageDown 交给宿主算一步，Home / End 停在两端。
calls.length = 0;
for (const code of ["ArrowLeft", "ArrowDown", "ArrowRight", "ArrowUp", "PageDown", "PageUp", "Home", "End"]) assert.ok(key(code), `${code} 被认领`);
assert.deepEqual(calls, ["step(-1.000, false)", "step(-1.000, false)", "step(1.000, false)", "step(1.000, false)", "step(-1.000, true)", "step(1.000, true)",
  "select(0.000, true)", "select(1.000, true)"]);

console.log("control slider tests passed");
