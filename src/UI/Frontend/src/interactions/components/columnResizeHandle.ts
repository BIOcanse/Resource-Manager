// Column resize uses handle position; the host converts it to stored width.
// Positions use screen pixels relative to the header.
// Weighted column layout makes stored width nonlinear in handle position.

// Preview while dragging; commit once on release. Keyboard steps use stored width.
// Cancel restores stored width because position does not uniquely determine it.


import { CURRENT, LANDING, STOP_POINT } from "../protocol/definition.ts";
import { CUBIC, NO_FIELD, toTerminal } from "../protocol/field.ts";
import { call, CLAIM, dragBasics, FOCUS, stopAt } from "./common.ts";
import type { PartDefinition } from "./common.ts";

/** Position bounds exceed the viewport and match the geometry endpoints. */
export const columnEdgeBound = 100000;

const drag = dragBasics({ cancel: [call("cancel")] });

export const columnResizeHandleDefinition: PartDefinition<number, "平面移动"> = {
  id: "resource-table-column-resize",
  name: "列宽把手",
  motion: "平面移动",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], to: "按下", out: [CLAIM, FOCUS] },
    { from: ["静止"], on: ["key.ArrowLeft↑", "key.ArrowLeft⟳"], to: "=", out: [CLAIM, call("step", -1)] },
    { from: ["静止"], on: ["key.ArrowRight↑", "key.ArrowRight⟳"], to: "=", out: [CLAIM, call("step", 1)] },
    { from: ["静止"], on: ["key.ArrowLeft↓", "key.ArrowRight↓"], to: "=" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [call("resize", CURRENT, false)] },
    { from: ["按下"], on: ["pointer.left↓"], to: "静止", note: "单击不改宽度" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [call("resize", CURRENT, false)], note: "连续调用" },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [stopAt(LANDING)] },
    ...drag.rows
  ],
  // Release stops in place; the host clamps the resulting width.
  channel: {
    id: "edge", label: "右边缘位置", unit: "px", min: -columnEdgeBound, max: columnEdgeBound, step: 1, hand: drag.hand, field: toTerminal(NO_FIELD, CUBIC),
    stopCall: call("resize", STOP_POINT, true)
  },
  ports: [
    { name: "resize", params: ["edge", "final"], continuous: true },
    { name: "step", params: ["direction"], continuous: false },
    { name: "cancel", params: [], continuous: false }
  ],
  values: {},
  // The host draws column widths, so the channel must synchronize immediately.
  // Immediate synchronization makes the next drag start at the actual handle position.
  params: { settleTime: 0 }
};
