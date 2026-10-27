// 控制面的数值滑条（风扇固定转速、功率上限这类“一个数”的控制点）。画面仍是原生 <input type="range">，
// 外观不变；这份定义只接管它的行为（原生 range 每走一格就写一次期望状态，也没有 Esc、右键取消）。
// 写法照曲线点（curvePoint.ts）和复刻的 Radix Slider：组件是滑块，它的外框是滑块此刻所在的那一块（呈现层登记，原生滑块没有元素）。
// 按在滑块上（指针在组件内=是）是抓住：按下时手所在处取滑块圆心，拖动中保持按下时指针相对圆心的偏移，和原生 range 一样不跳；
// 按在滑轨别处（否）记下指针处，不改值，离开拖动框起滑块圆心在指针下。拖动中连续调用 select(位置, 否)，宿主只拿来预览；
// 松开停在落点，调用一次 select(停住点, 是)，这才写期望状态。单击（没离开拖动框就松开）：滑块上不改值，滑轨上到那里（指针处）。
// Esc、右键、指针离开响应区取消：调用 cancel()，什么都不写，宿主值没变，动画风格选的弹簧或缓动把滑块带回拖动前的值。
// 键照原生 range：左、下减一步，右、上加一步，PageDown / PageUp 减、加一大步，调用 step(方向, 是否大步)，
// 由宿主按当前值算（一步和一大步取决于量程，宿主才知道）；Home / End 停在两端。
// 通道是滑块在量程里的比例（0–1，几何两端是值最小、最大时的滑块圆心，换算是直线），一份定义给所有量程用；
// 比例换成值、按档位取整都在宿主一处做（controlNumber.ts），和数字框共用同一个说法。

import { CURRENT, LANDING, STOP_POINT } from "../protocol/definition.ts";
import type { Definition } from "../protocol/definition.ts";
import { NO_FIELD, toTerminal } from "../protocol/field.ts";
import { POINTER_AT } from "../protocol/values.ts";
import { call, CLAIM, dragBasics, FOCUS, JUMP_TO_POINTER, stopAt } from "./common.ts";
import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

/**
 * 原生 range 滑块的半宽（px，Chromium 的滑块宽 16 px）：值最小、最大时滑块圆心离两端各这么远，
 * 滑块的外框是圆心左右各这么宽。呈现层的几何、外框和 control.css 实际值刻度让出的 8px 都是这个数。
 */
export const thumbHalfWidth = 8;

const KEYS = ["key.ArrowLeft", "key.ArrowDown", "key.ArrowRight", "key.ArrowUp", "key.PageDown", "key.PageUp", "key.Home", "key.End"] as const;
const KEY_CHANGES = KEYS.flatMap((k) => [`${k}↑`, `${k}↓`, `${k}⟳`]);
const select = (value: string | number, final: boolean) => call("select", value, final);
const step = (direction: number, big: boolean) => [CLAIM, call("step", direction, big)];
// 按住时的方向键明确忽略（认领）：不然浏览器会让原生 range 自己走一格。
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
  // 没有输出映射点，平时没有力场：松手不带速度，停在松手处；宿主按档位取整后由动画风格选的弹簧或缓动带过去。
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
