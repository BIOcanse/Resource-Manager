// The native range supplies appearance; this definition controls preview, commit, and cancellation.

// The registered component bounds describe the thumb rather than the entire track.
// Grabbing the thumb preserves the pointer offset; grabbing the track follows the pointer.
// Dragging only previews the value.
// Release commits once; a track click selects its position.
// Cancellation restores the host value without writing a new desired state.
// The host computes keyboard steps for the current range.

// The channel is a 0-1 ratio between the endpoint thumb centers.
// The host converts and rounds ratios using the same rules as the number input.

import { CURRENT, LANDING, STOP_POINT } from "../protocol/definition.ts";
import type { Definition } from "../protocol/definition.ts";
import { NO_FIELD, toTerminal } from "../protocol/field.ts";
import { POINTER_AT } from "../protocol/values.ts";
import { call, CLAIM, dragBasics, FOCUS, JUMP_TO_POINTER, stopAt } from "./common.ts";
import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

/** Half the native thumb width in pixels; shared by geometry and range styling. */
export const thumbHalfWidth = 8;

const KEYS = ["key.ArrowLeft", "key.ArrowDown", "key.ArrowRight", "key.ArrowUp", "key.PageDown", "key.PageUp", "key.Home", "key.End"] as const;
const KEY_CHANGES = KEYS.flatMap((k) => [`${k}↑`, `${k}↓`, `${k}⟳`]);
const select = (value: string | number, final: boolean) => call("select", value, final);
const step = (direction: number, big: boolean) => [CLAIM, call("step", direction, big)];
// Claim held arrow keys to prevent native range stepping.
const drag = dragBasics({ cancel: [call("cancel")], ignore: KEY_CHANGES });

export const controlSliderDefinition: PartDefinition<number, "平面移动"> = {
  id: "control-number-slider",
  name: "数值滑条",
  motion: "平面移动",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], when: { "指针在组件内": true }, to: "按下", out: [CLAIM, FOCUS],
      note: "按在滑块上：抓住，保持指针相对圆心的偏移，不跳" },
    { from: ["静止"], on: ["pointer.left↑"], when: { "指针在组件内": false }, to: "按下", out: [CLAIM, FOCUS, JUMP_TO_POINTER],
      note: "按在滑轨别处：手记在指针处，按下不由手带，值不变" },
    { from: ["静止"], on: ["key.ArrowLeft↑", "key.ArrowLeft⟳", "key.ArrowDown↑", "key.ArrowDown⟳"], to: "=", out: step(-1, false) },
    { from: ["静止"], on: ["key.ArrowRight↑", "key.ArrowRight⟳", "key.ArrowUp↑", "key.ArrowUp⟳"], to: "=", out: step(1, false) },
    { from: ["静止"], on: ["key.PageDown↑", "key.PageDown⟳"], to: "=", out: step(-1, true) },
    { from: ["静止"], on: ["key.PageUp↑", "key.PageUp⟳"], to: "=", out: step(1, true) },
    { from: ["静止"], on: ["key.Home↑", "key.Home⟳"], to: "=", out: [CLAIM, stopAt(0)] },
    { from: ["静止"], on: ["key.End↑", "key.End⟳"], to: "=", out: [CLAIM, stopAt(1)] },
    { from: ["静止"], on: KEYS.map((k) => `${k}↓`), to: "=" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [select(CURRENT, false)],
      note: "由手带：手 = 按下时手所在处 + 指针的位移" },
    { from: ["按下"], on: ["pointer.left↓"], when: { "指针在组件内": true }, to: "静止", note: "单击滑块：只取得焦点，不改值" },
    { from: ["按下"], on: ["pointer.left↓"], when: { "指针在组件内": false }, to: "静止", out: [stopAt("指针处")],
      note: "单击滑轨：滑块到那里（指针处可以在两端外，宿主取整时限制在量程里）" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [select(CURRENT, false)], note: "连续调用，宿主只预览" },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [stopAt(LANDING)], note: "松开：写一次" },
    ...drag.rows
  ],
  // Release stops in place; host rounding is applied through the selected spring or easing.
  channel: {
    id: "ratio", label: "量程里的位置", unit: "", min: 0, max: 1, hand: drag.hand, field: toTerminal(NO_FIELD, carryField("control")),
    stopCall: select(STOP_POINT, true)
  },
  ports: [
    { name: "select", params: ["ratio", "final"], continuous: true },
    { name: "step", params: ["direction", "big"], continuous: false },
    { name: "cancel", params: [], continuous: false }
  ],
  values: { "指针处": POINTER_AT.fn as Definition["values"][string] }
};
