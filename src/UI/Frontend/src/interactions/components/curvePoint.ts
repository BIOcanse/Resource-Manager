// 曲线点：风扇曲线上的一个点（协议第 4.4 节第 2 处迁移，来自 Lodestone components/curvePoint.ts，未改行为）。只能上下动，步长 1%，没有输出映射点。
// 按在点上是抓住，不跳；曲线图把空白处的按下交过来时（指针在组件内=否），跳到指针处接着拖。
// 拖动中每次移动都调用（连续），松手停在落点，取消回起点的值，停住时调用。键盘和产品一致：方向键 ±1，Shift 加方向键 ±10，Home / End。

import { CURRENT, LANDING, ORIGIN, STOP_POINT } from "../protocol/definition.ts";
import { NO_FIELD, toTerminal } from "../protocol/field.ts";
import { call, CLAIM, dragBasics, FOCUS, JUMP_TO_POINTER, stopAt } from "./common.ts";
import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

const STEP = 1;
const BIG_STEP = 10;
const KEYS = ["key.ArrowUp", "key.ArrowDown", "key.Home", "key.End"] as const;
const KEY_CHANGES = KEYS.flatMap((k) => [`${k}↑`, `${k}↓`, `${k}⟳`]);
const select = (value: string | number) => call("select", value);
const drag = dragBasics({ cancel: [stopAt(ORIGIN)], ignore: KEY_CHANGES });

export function fixedAxisCurvePointDefinition(options: {
  minimum: number; maximum: number; step: number; coarseStep: number; unit: string;
  commit?: boolean; backendKeyboard?: boolean;
}): PartDefinition<number, "平面移动"> {
const { minimum, maximum, step, coarseStep, unit } = options;
const clampValue = (value: number) => Math.min(maximum, Math.max(minimum, value));
const begin = options.commit ? [call("begin")] : [];
const stepOutput = (direction: number, coarse: boolean, value: string) => options.backendKeyboard
  ? call("step", direction, coarse) : stopAt(value);
return {
  id: "curve-point",
  name: "曲线点",
  motion: "平面移动",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], when: { "指针在组件内": true }, to: "按下", out: [CLAIM, ...begin, FOCUS], note: "按在点上：抓住，不跳" },
    { from: ["静止"], on: ["pointer.left↑"], when: { "指针在组件内": false }, to: "拖动",
      out: [CLAIM, ...begin, FOCUS, JUMP_TO_POINTER, select(CURRENT)], note: "按在空白处（曲线图交过来）：跳到指针处，接着拖" },
    { from: ["静止"], on: ["key.ArrowUp↑", "key.ArrowUp⟳"], when: { "Shift 按住": false }, to: "=", out: [CLAIM, stepOutput(1, false, "上移一步")] },
    { from: ["静止"], on: ["key.ArrowUp↑", "key.ArrowUp⟳"], when: { "Shift 按住": true }, to: "=", out: [CLAIM, stepOutput(1, true, "上移一大步")] },
    { from: ["静止"], on: ["key.ArrowDown↑", "key.ArrowDown⟳"], when: { "Shift 按住": false }, to: "=", out: [CLAIM, stepOutput(-1, false, "下移一步")] },
    { from: ["静止"], on: ["key.ArrowDown↑", "key.ArrowDown⟳"], when: { "Shift 按住": true }, to: "=", out: [CLAIM, stepOutput(-1, true, "下移一大步")] },
    { from: ["静止"], on: ["key.Home↑", "key.Home⟳"], to: "=", out: options.backendKeyboard ? [] : [CLAIM, stopAt(minimum)] },
    { from: ["静止"], on: ["key.End↑", "key.End⟳"], to: "=", out: options.backendKeyboard ? [] : [CLAIM, stopAt(maximum)] },
    { from: ["静止"], on: ["key.ArrowUp↓", "key.ArrowDown↓", "key.Home↓", "key.End↓"], to: "=" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [select(CURRENT)] },
    { from: ["按下"], on: ["pointer.left↓"], to: "静止", note: "单击只取得焦点，不改值" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [select(CURRENT)], note: "连续调用" },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [stopAt(LANDING)] },
    ...drag.rows,
  ],
  // 没有输出映射点，平时没有力场：松手不带速度，停在松手处；带向终态（键盘一步、取消回起点）时由动画风格选的弹簧或缓动带过去。
  channel: {
    id: "percent", label: "纵轴", unit, min: minimum, max: maximum, step, hand: drag.hand, field: toTerminal(NO_FIELD, carryField("control")),
    stopCall: options.commit ? call("commit", STOP_POINT) : select(STOP_POINT),
  },
  ports: [{ name: "select", params: ["percent"], continuous: true },
    ...(options.commit ? [{ name: "begin", params: [], continuous: false }, { name: "commit", params: ["value"], continuous: false }] : []),
    ...(options.backendKeyboard ? [{ name: "step", params: ["direction", "coarse"], continuous: false }] : [])],
  values: {
    "上移一步": (ctx) => ({ value: clampValue(ctx.hostValue + step), explain: `+${step} ${unit}` }),
    "下移一步": (ctx) => ({ value: clampValue(ctx.hostValue - step), explain: `−${step} ${unit}` }),
    "上移一大步": (ctx) => ({ value: clampValue(ctx.hostValue + coarseStep), explain: `+${coarseStep} ${unit}` }),
    "下移一大步": (ctx) => ({ value: clampValue(ctx.hostValue - coarseStep), explain: `−${coarseStep} ${unit}` }),
  },
};
}

export const curvePointDefinition = fixedAxisCurvePointDefinition({
  minimum: 0, maximum: 100, step: STEP, coarseStep: BIG_STEP, unit: "%"
});
