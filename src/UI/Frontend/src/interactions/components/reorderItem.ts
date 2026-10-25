// 拖动排序项（协议第 4.4 节第 3 处；写法照 Lodestone tests/engine.test.ts 的“拖动排序项的写法”）。
// 物体不跟手（hand 为空）：通道是平面，几何取 1 个单位 = 1 个屏幕 px，取值「指针处」就是指针的屏幕坐标。
// 开始拖和拖动中调用 over(指针处)，松开认领并调用 commit(指针处)（拖完不触发点击），取消调用 cancel()。
// 指针下是哪一项、源和目标的 id 只在宿主映射里（interactions/solid/reorderGroup.ts），不进定义。
//
// 和引擎测试那份的差别：
// - 按下时取得焦点（和列宽把手一样）：键盘变化按焦点路由，独占指针不包括键盘，不取得焦点时拖动中按 Esc 到不了这一项。
//   项的元素由宿主映射设成可以取得焦点（tabIndex = -1，不进 Tab 次序）。
// - 输入丢失当作取消（协议第 4.4 节第 3 处“失焦仍当作取消，和现在一样：
//   排序是一次提交，校正出来的松开可能落在用户没想放的地方”），用拖动公共行的 cancelOnLoss。

import type { Definition } from "../protocol/definition.ts";
import { HOLD } from "../protocol/field.ts";
import { POINTER_AT } from "../protocol/values.ts";
import type { Vec } from "../protocol/vec.ts";
import { call, CLAIM, dragBasics, FOCUS, LOSS_EVENTS } from "./common.ts";

/** 通道边界（屏幕 px）：远大于屏幕，几何的两角就是边界的两角，所以通道的值就是屏幕坐标。 */
export const REORDER_BOUND = 100000;
const drag = dragBasics({ cancel: [call("cancel")], cancelOnLoss: LOSS_EVENTS });

export const reorderItemDefinition: Definition<Vec> = {
  id: "reorder-item",
  name: "拖动排序项",
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
