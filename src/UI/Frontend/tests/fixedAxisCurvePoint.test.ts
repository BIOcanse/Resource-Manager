import assert from "node:assert/strict";
import { curvePointDefinition, fixedAxisCurvePointDefinition } from "../src/interactions/components/curvePoint.ts";
import { Runtime } from "../src/interactions/protocol/runtime.ts";
import type { Host, Platform, Presentation } from "../src/interactions/protocol/runtime.ts";
import type { RawInput } from "../src/interactions/protocol/input.ts";
import { Trace } from "../src/interactions/protocol/trace.ts";
import type { PartDefinition } from "../src/interactions/components/common.ts";
function harness(definition: PartDefinition<number, "平面移动">) {
  const element = {}, calls: { port: string; args: readonly unknown[] }[] = [];
  let value = 50, time = 0;
  const listeners = new Set<(old: number, next: number, why: string) => void>();
  const platform: Platform = { contains: (a, b) => a === b,
    rect: () => ({ left: -5, top: 100 - value - 5, right: 5, bottom: 100 - value + 5 }),
    capture() {}, release() {}, focus() {}, requestFrame() {}, responseZone: () => ({ left: -1000, top: -1000, right: 1000, bottom: 1000 }) };
  const host: Host<number> = { value: () => value, enabled: () => true, subscribe: listener => { listeners.add(listener); return () => listeners.delete(listener); },
    call: (port, args) => { calls.push({ port, args }); if (port === "select" || port === "commit") {
      const old = value; value = Number(args[0]); for (const listener of listeners) listener(old, value, "test host");
    } } };
  const view: Presentation = { element, bindingLabel: "test", geometry: () => ({ center: "point", atMin: { x: 0, y: 100 }, atMax: { x: 0, y: 0 } }), render() {}, aria: () => "" };
  const runtime = new Runtime({ doubleClickTimeMs: 500, doubleClickWidthPx: 4, doubleClickHeightPx: 4, dragWidthPx: 4, dragHeightPx: 4,
    keyboardDelayStep: 1, keyboardSpeedStep: 31, wheelScrollLines: 3 }, new Trace(), platform, { mode: "optimized" });
  runtime.add("shared curve point", definition, view, host);
  const modifiers = { shift: false, ctrl: false, alt: false, meta: false };
  function pointer(y: number, buttons: number, dom = "pointermove") {
    time += 10; runtime.feed({ type: "pointer", t: time, x: 0, y, buttons, pointerId: 1, dom, modifiers, target: element, event: { preventDefault() {} } } satisfies RawInput);
  }
  function key(code: string, shift = false) {
    // The input protocol observes modifier key levels; event flags only correct a missing release.
    // Reproduce the browser's physical Shift press before the modified arrow event.
    if (shift) { time += 10; runtime.feed({ type: "key", t: time, code: "ShiftLeft", key: "Shift", down: true, repeat: false,
      modifiers: { ...modifiers, shift: true }, target: element, event: { preventDefault() {} } } satisfies RawInput); }
    for (const down of [true, false]) { time += 10; runtime.feed({ type: "key", t: time, code, key: code, down, repeat: false,
      modifiers: { ...modifiers, shift }, target: element, event: { preventDefault() {} } } satisfies RawInput); }
    if (shift) { time += 10; runtime.feed({ type: "key", t: time, code: "ShiftLeft", key: "Shift", down: false, repeat: false,
      modifiers, target: element, event: { preventDefault() {} } } satisfies RawInput); }
    for (let i = 0; i < 100; i++) { time += 16; runtime.frame(time); }
  }
  return { calls, pointer, key, value: () => value };
}
const fan = harness(curvePointDefinition);
fan.pointer(50, 1, "pointerdown"); fan.pointer(50, 0, "pointerup");
assert.deepEqual(fan.calls, [], "Clicking a fan point only focuses; it does not jump");
fan.key("ArrowUp"); assert.equal(fan.value(), 51, "Fan retains one-percent fine step");
fan.key("ArrowDown", true); assert.equal(fan.value(), 41, "Fan retains Shift ten-percent coarse step");
fan.key("Home"); assert.equal(fan.value(), 0); fan.key("End"); assert.equal(fan.value(), 100);
const gpu = harness(fixedAxisCurvePointDefinition({ minimum: 0, maximum: 100, step: 5, coarseStep: 20, unit: "kHz", commit: true, backendKeyboard: true }));
gpu.pointer(50, 1, "pointerdown"); gpu.pointer(20, 1); gpu.pointer(20, 0, "pointerup");
assert.equal(gpu.calls.filter(call => call.port === "commit").length, 1, "One drag emits one backend history command");
assert.ok(gpu.calls.some(call => call.port === "select"), "Movement provides a gesture preview");
const before = gpu.value(); gpu.key("ArrowUp", true);
assert.deepEqual(gpu.calls.at(-1), { port: "step", args: [1, true] }, "Keyboard step policy remains on the backend");
assert.equal(gpu.value(), before, "A backend step does not invent local draft values");
gpu.calls.length = 0; gpu.pointer(100 - before, 1, "pointerdown"); gpu.pointer(5, 1); gpu.key("Escape");
assert.equal(gpu.calls.filter(call => call.port === "commit").at(-1)?.args[0], before, "Escape restores this gesture's original point");
console.log("Shared fan and GPU curve point interactions passed");
