// Reordering tracks the pointer without moving the channel object.
// The plane channel uses screen pixels, so pointer values are screen coordinates.
// Drag calls over; release claims the event and commits once; cancel calls cancel.
// The host owns item identity and hit mapping.


// Focus on press so Esc reaches the dragged item; pointer ownership does not route keys.
// The host uses tabIndex=-1 to allow focus without adding a Tab stop.
// Input loss cancels because a corrected release could commit an unintended target.


import type { Definition } from "../protocol/definition.ts";
import { HOLD } from "../protocol/field.ts";
import { POINTER_AT } from "../protocol/values.ts";
import type { Vec } from "../protocol/vec.ts";
import { call, CLAIM, dragBasics, FOCUS, LOSS_EVENTS } from "./common.ts";
import type { PartDefinition } from "./common.ts";

/** Geometry and channel bounds share screen-pixel coordinates. */
export const REORDER_BOUND = 100000;
const drag = dragBasics({ cancel: [call("cancel")], cancelOnLoss: LOSS_EVENTS });

export const reorderItemDefinition: PartDefinition<Vec, "平面移动"> = {
  id: "reorder-item",
  name: "拖动排序项",
  motion: "平面移动",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], to: "按下", out: [CLAIM, FOCUS], note: "取得焦点：键盘按焦点路由，拖动中的 Esc 才到得了这一项" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [call("over", "指针处")] },
    { from: ["按下"], on: ["pointer.left↓"], to: "静止", note: "单击，不排序" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [call("over", "指针处")] },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [CLAIM, call("commit", "指针处")], note: "认领：拖完不触发点击" },
    ...drag.rows,
  ],
  channel: { id: "pointer", label: "指针处", unit: "px", min: { x: -REORDER_BOUND, y: -REORDER_BOUND }, max: { x: REORDER_BOUND, y: REORDER_BOUND }, hand: [], field: HOLD },
  ports: [{ name: "over", params: ["at"], continuous: true }, { name: "commit", params: ["at"], continuous: false }, { name: "cancel", params: [], continuous: false }],
  values: { "指针处": POINTER_AT.fn as Definition<Vec>["values"][string] },
};
