// 拖动排序项的定义（src/interactions/components/reorderItem.ts）：过机器检查；拖动中 over(指针处)、松开认领并 commit(指针处)；
// Esc、失焦取消（失焦后不等校正出的松开，不提交）；在拖动框里的小移动当作单击，不调用。
import assert from "node:assert/strict";
import { reorderItemDefinition, REORDER_BOUND } from "../src/interactions/components/reorderItem.ts";
import { checkDefinition } from "../src/interactions/protocol/check.ts";
import type { Definition } from "../src/interactions/protocol/definition.ts";
import type { RawInput } from "../src/interactions/protocol/input.ts";
import { Runtime } from "../src/interactions/protocol/runtime.ts";
import type { Host, Platform, Presentation } from "../src/interactions/protocol/runtime.ts";
import { Trace } from "../src/interactions/protocol/trace.ts";
import type { Value, Vec } from "../src/interactions/protocol/vec.ts";

const NO_MODS = { shift: false, ctrl: false, alt: false, meta: false };
const SYSTEM = {
  doubleClickTimeMs: 500, doubleClickWidthPx: 4, doubleClickHeightPx: 4, dragWidthPx: 4, dragHeightPx: 4,
  keyboardDelayStep: 1, keyboardSpeedStep: 31, wheelScrollLines: 3,
};
const el = {};
const platform: Platform = {
  contains: (a, b) => a === b,
  rect: () => ({ left: 0, top: 0, right: 100, bottom: 30 }),
  capture: () => {}, release: () => {}, focus: () => {},
  responseZone: () => ({ left: -10000, top: -10000, right: 10000, bottom: 10000 }),
  requestFrame: () => {},
};
const calls: string[] = [];
const host: Host<Vec> = {
  value: () => ({ x: 0, y: 0 }), enabled: () => true,
  call: (port, args) => { calls.push(`${port}(${args.map((a) => JSON.stringify(a)).join(", ")})`); },
  subscribe: () => () => {},
};
const geometry = { center: "指针", atMin: { x: -REORDER_BOUND, y: -REORDER_BOUND }, atMax: { x: REORDER_BOUND, y: REORDER_BOUND } };
const view: Presentation<Vec> = { element: el, bindingLabel: "源半透明", geometry: () => geometry, render() {}, aria: () => "" };
const rt = new Runtime(SYSTEM, new Trace(), platform, { mode: "optimized" });
const inst = rt.add("排序项", reorderItemDefinition, view, host);
const check = checkDefinition(reorderItemDefinition as unknown as Definition<Value>, geometry, inst.params);
assert.ok(check.ok, `机器检查：${check.lines.join("；")}`);

const ptr = (t: number, x: number, y: number, buttons: number, dom = "pointermove") => {
  const event = { defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
  rt.feed({ type: "pointer", t, x, y, buttons, pointerId: 1, dom, modifiers: NO_MODS, target: el, event } satisfies RawInput);
  return event.defaultPrevented;
};
const loss = (t: number, id: "loss.blur" | "loss.focus") => rt.feed({ type: "loss", t, id, target: null, event: {} } satisfies RawInput);

// 拖动：开始拖和拖动中 over，松开认领并 commit；松开处和上一次不同时先多一次 over。
ptr(0, 10, 10, 0);
ptr(10, 10, 10, 1, "pointerdown");
ptr(20, 40, 12, 1);
ptr(30, 150, 60, 1);
assert.deepEqual(inst.ch.x, { x: 0, y: 0 }, "物体不跟手");
assert.ok(ptr(40, 160, 64, 0, "pointerup"), "拖完的松开被认领");
assert.deepEqual(calls, ['over({"x":40,"y":12})', 'over({"x":150,"y":60})', 'over({"x":160,"y":64})', 'commit({"x":160,"y":64})']);

// Esc 取消。
calls.length = 0;
ptr(50, 10, 10, 1, "pointerdown");
ptr(60, 80, 10, 1);
rt.feed({ type: "key", t: 70, code: "Escape", key: "Escape", down: true, repeat: false, modifiers: NO_MODS, target: el });
ptr(80, 90, 10, 0, "pointerup");
assert.deepEqual(calls, ['over({"x":80,"y":10})', "cancel()"], "Esc 取消，之后的松开不提交");

// 失焦取消：回来后指针报告左键已松开，也不提交。
calls.length = 0;
ptr(100, 10, 10, 1, "pointerdown");
ptr(110, 80, 10, 1);
loss(120, "loss.blur");
loss(130, "loss.focus");
ptr(140, 120, 10, 0);
assert.deepEqual(calls, ['over({"x":80,"y":10})', "cancel()"], "失焦取消");
assert.equal(inst.mode, "静止");

// 拖动框里的小移动：当作单击，不调用，松开不认领（点击照常）。
calls.length = 0;
ptr(200, 10, 10, 1, "pointerdown");
ptr(210, 11, 11, 1);
assert.equal(ptr(220, 11, 11, 0, "pointerup"), false, "单击的松开不认领");
assert.deepEqual(calls, []);

console.log("reorder item tests passed");
