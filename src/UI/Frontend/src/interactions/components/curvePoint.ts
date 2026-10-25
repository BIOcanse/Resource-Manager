// 曲线点：风扇曲线上的一个点（协议第 4.4 节第 2 处迁移，来自 Lodestone components/curvePoint.ts，未改行为）。只能上下动，步长 1%，没有输出映射点。
// 按在点上是抓住，不跳；曲线图把空白处的按下交过来时（指针在组件内=否），跳到指针处接着拖。
// 拖动中每次移动都调用（连续），松手停在落点，取消回起点的值，停住时调用。键盘和产品一致：方向键 ±1，Shift 加方向键 ±10，Home / End。

import { CURRENT, LANDING, ORIGIN, STOP_POINT } from "../protocol/definition.ts";
import type { Definition } from "../protocol/definition.ts";
import { CUBIC, NO_FIELD, toTerminal } from "../protocol/field.ts";
import { call, CLAIM, dragBasics, FOCUS, JUMP_TO_POINTER, stopAt } from "./common.ts";

const STEP = 1;
const BIG_STEP = 10;
const clamp = (v: number) => Math.min(100, Math.max(0, v));
const KEYS = ["key.ArrowUp", "key.ArrowDown", "key.Home", "key.End"] as const;
const KEY_CHANGES = KEYS.flatMap((k) => [`${k}↑`, `${k}↓`, `${k}⟳`]);
const select = (value: string | number) => call("select", value);
const drag = dragBasics({ cancel: [stopAt(ORIGIN)], ignore: KEY_CHANGES });

export const curvePointDefinition: Definition = {
  id: "curve-point",
  name: "曲线点",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], when: { "指针在组件内": true }, to: "按下", out: [CLAIM, FOCUS], note: "按在点上：抓住，不跳" },
    { from: ["静止"], on: ["pointer.left↑"], when: { "指针在组件内": false }, to: "拖动",
      out: [CLAIM, FOCUS, JUMP_TO_POINTER, select(CURRENT)], note: "按在空白处（曲线图交过来）：跳到指针处，接着拖" },
    { from: ["静止"], on: ["key.ArrowUp↑", "key.ArrowUp⟳"], when: { "Shift 按住": false }, to: "=", out: [CLAIM, stopAt("上移一步")] },
    { from: ["静止"], on: ["key.ArrowUp↑", "key.ArrowUp⟳"], when: { "Shift 按住": true }, to: "=", out: [CLAIM, stopAt("上移一大步")] },
    { from: ["静止"], on: ["key.ArrowDown↑", "key.ArrowDown⟳"], when: { "Shift 按住": false }, to: "=", out: [CLAIM, stopAt("下移一步")] },
    { from: ["静止"], on: ["key.ArrowDown↑", "key.ArrowDown⟳"], when: { "Shift 按住": true }, to: "=", out: [CLAIM, stopAt("下移一大步")] },
    { from: ["静止"], on: ["key.Home↑", "key.Home⟳"], to: "=", out: [CLAIM, stopAt(0)] },
    { from: ["静止"], on: ["key.End↑", "key.End⟳"], to: "=", out: [CLAIM, stopAt(100)] },
    { from: ["静止"], on: ["key.ArrowUp↓", "key.ArrowDown↓", "key.Home↓", "key.End↓"], to: "=" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [select(CURRENT)] },
    { from: ["按下"], on: ["pointer.left↓"], to: "静止", note: "单击只取得焦点，不改值" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [select(CURRENT)], note: "连续调用" },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [stopAt(LANDING)] },
    ...drag.rows,
  ],
  // 没有输出映射点，没有力场：松手不带速度，停在松手处。
  channel: {
    id: "percent", label: "转速", unit: "%", min: 0, max: 100, step: STEP, hand: drag.hand, field: toTerminal(NO_FIELD, CUBIC),
    stopCall: select(STOP_POINT),
  },
  ports: [{ name: "select", params: ["percent"], continuous: true }],
  values: {
    "上移一步": (ctx) => ({ value: clamp(ctx.hostValue + STEP), explain: `宿主值 ${ctx.hostValue}% + ${STEP}%，限制在 0–100` }),
    "下移一步": (ctx) => ({ value: clamp(ctx.hostValue - STEP), explain: `宿主值 ${ctx.hostValue}% − ${STEP}%，限制在 0–100` }),
    "上移一大步": (ctx) => ({ value: clamp(ctx.hostValue + BIG_STEP), explain: `宿主值 ${ctx.hostValue}% + ${BIG_STEP}%（Shift），限制在 0–100` }),
    "下移一大步": (ctx) => ({ value: clamp(ctx.hostValue - BIG_STEP), explain: `宿主值 ${ctx.hostValue}% − ${BIG_STEP}%（Shift），限制在 0–100` }),
  },
};
