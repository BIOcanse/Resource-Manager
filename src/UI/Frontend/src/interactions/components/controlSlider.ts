// 控制面的数值滑条（风扇固定转速、功率上限这类“一个数”的控制点）。画面仍是原生 <input type="range">，
// 外观不变；这份定义只接管它的行为（原生 range 每走一格就写一次期望状态，也没有 Esc、右键取消）。
// 写法照复刻的 Radix Slider（Lodestone replicas）：按下记下指针处，不改值；离开拖动框起滑块圆心就在指针下，跟着拖，
// 拖动中连续调用 select(位置, 否)，宿主只拿来预览；松开停在落点，调用一次 select(停住点, 是)，这才写期望状态。
// 单击（没离开拖动框就松开）和原生 range 一样：点在滑块上不改值，点在滑轨别处滑块到那里（取值“单击处”）。
// Esc、右键、指针离开响应区取消：调用 cancel()，什么都不写，宿主值没变，三次曲线把滑块带回拖动前的值。
// 键照原生 range：左、下减一步，右、上加一步，PageDown / PageUp 减、加一大步，调用 step(方向, 是否大步)，
// 由宿主按当前值算（一步和一大步取决于量程，宿主才知道）；Home / End 停在两端。
// 通道是滑块在量程里的比例（0–1，几何两端是值最小、最大时的滑块圆心，换算是直线），一份定义给所有量程用；
// 比例换成值、按档位取整都在宿主一处做（controlNumber.ts），和数字框共用同一个说法。

import { CURRENT, LANDING, STOP_POINT } from "../protocol/definition.ts";
import type { Definition, ValueContext } from "../protocol/definition.ts";
import { CUBIC, NO_FIELD, toTerminal } from "../protocol/field.ts";
import { frameOf } from "../protocol/physics.ts";
import { call, CLAIM, dragBasics, FOCUS, JUMP_TO_POINTER, stopAt } from "./common.ts";

/**
 * 原生 range 滑块的半宽（px，Chromium 的滑块宽 16 px）：值最小、最大时滑块圆心离两端各这么远，
 * 指针离滑块圆心不超过它就是点在滑块上。呈现层的几何和 control.css 实际值刻度让出的 8px 都是这个数。
 */
export const thumbHalfWidth = 8;

const KEYS = ["key.ArrowLeft", "key.ArrowDown", "key.ArrowRight", "key.ArrowUp", "key.PageDown", "key.PageUp", "key.Home", "key.End"] as const;
const KEY_CHANGES = KEYS.flatMap((k) => [`${k}↑`, `${k}↓`, `${k}⟳`]);
const select = (value: string | number, final: boolean) => call("select", value, final);
const step = (direction: number, big: boolean) => [CLAIM, call("step", direction, big)];
// 按住时的方向键明确忽略（认领）：不然浏览器会让原生 range 自己走一格。
const drag = dragBasics({ cancel: [call("cancel")], ignore: KEY_CHANGES });

/** 单击处：指针在滑块上就是宿主值（不改），否则是指针处换成的比例，限制在 0–1。 */
function clickedAt(ctx: ValueContext<number>) {
  const p = ctx.input.position();
  if (!ctx.geometry) return { value: ctx.hostValue, explain: "没有几何，取宿主值" };
  const frame = frameOf(ctx.def.channel, ctx.geometry);
  // 滑块画在通道此刻的位置（按下时运动已停在这里）。
  const thumb = frame.at(ctx.channel.x);
  const away = Math.abs(p.x - thumb.x);
  if (away <= thumbHalfWidth) {
    return { value: ctx.hostValue, explain: `指针离滑块圆心 ${away.toFixed(1)} px，不超过半个滑块 ${thumbHalfWidth} px：点在滑块上，取宿主值 ${ctx.hostValue}` };
  }
  // 一条轴的通道，valueOf 给的是数。
  const value = Math.min(1, Math.max(0, frame.valueOf(p) as number));
  return { value, explain: `指针离滑块圆心 ${away.toFixed(1)} px，点在滑轨上：指针处换成 ${value.toFixed(4)}，限制在 0–1` };
}

export const controlSliderDefinition: Definition = {
  id: "control-number-slider",
  name: "数值滑条",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], to: "按下", out: [CLAIM, FOCUS, JUMP_TO_POINTER], note: "手记在指针处，按下不由手带，值不变" },
    { from: ["静止"], on: ["key.ArrowLeft↑", "key.ArrowLeft⟳", "key.ArrowDown↑", "key.ArrowDown⟳"], to: "=", out: step(-1, false) },
    { from: ["静止"], on: ["key.ArrowRight↑", "key.ArrowRight⟳", "key.ArrowUp↑", "key.ArrowUp⟳"], to: "=", out: step(1, false) },
    { from: ["静止"], on: ["key.PageDown↑", "key.PageDown⟳"], to: "=", out: step(-1, true) },
    { from: ["静止"], on: ["key.PageUp↑", "key.PageUp⟳"], to: "=", out: step(1, true) },
    { from: ["静止"], on: ["key.Home↑", "key.Home⟳"], to: "=", out: [CLAIM, stopAt(0)] },
    { from: ["静止"], on: ["key.End↑", "key.End⟳"], to: "=", out: [CLAIM, stopAt(1)] },
    { from: ["静止"], on: KEYS.map((k) => `${k}↓`), to: "=" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [select(CURRENT, false)],
      note: "由手带：滑块圆心到指针下" },
    { from: ["按下"], on: ["pointer.left↓"], to: "静止", out: [stopAt("单击处")], note: "单击：点在滑块上不改，点在滑轨上到那里" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [select(CURRENT, false)], note: "连续调用，宿主只预览" },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [stopAt(LANDING)], note: "松开：写一次" },
    ...drag.rows
  ],
  // 没有输出映射点，没有力场：松手不带速度，停在松手处；宿主按档位取整后由三次曲线带过去。
  channel: {
    id: "ratio", label: "量程里的位置", unit: "", min: 0, max: 1, hand: drag.hand, field: toTerminal(NO_FIELD, CUBIC),
    stopCall: select(STOP_POINT, true)
  },
  ports: [
    { name: "select", params: ["ratio", "final"], continuous: true },
    { name: "step", params: ["direction", "big"], continuous: false },
    { name: "cancel", params: [], continuous: false }
  ],
  values: { "单击处": clickedAt }
};
