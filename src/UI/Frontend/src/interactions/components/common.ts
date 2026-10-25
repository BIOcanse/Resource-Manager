// 几个组件共用的写法：输入变化清单、常用输出，以及拖动组件的公共部分。
// 只是把同一串东西写一次；生成的行和手写的一样列在转移表里、一样过机器检查。

import type { Call, Mode, Output, Row } from "../protocol/definition.ts";

/** 输入丢失事件（协议第 2.5 节）。 */
export const LOSS_EVENTS = ["loss.blur", "loss.focus", "loss.hidden", "loss.visible", "loss.pointercancel", "loss.lostcapture", "loss.leftzone"] as const;

/** 按住左键时其余指针键的变化（左键按住，所以没有左键 ↑）。 */
export const OTHER_BUTTONS = [
  "pointer.right↓", "pointer.middle↑", "pointer.middle↓", "pointer.back↑", "pointer.back↓", "pointer.forward↑", "pointer.forward↓",
] as const;

export const CLAIM: Output = { tag: "框架", act: "认领" };
export const FOCUS: Output = { tag: "框架", act: "焦点" };
export const JUMP_TO_POINTER: Output = { tag: "动画", signal: "跳到指针处" };
/** 这一格不收，交给外层（协议第 4.2 节）。 */
export const PASS: Output = { tag: "框架", act: "交给外层" };

/** 业务调用：参数写取值的名字（字符串），或数字、真假字面量。 */
export const call = (port: string, ...args: readonly (string | number | boolean)[]): Call =>
  ({ tag: "业务", port, args: args.map((a) => (typeof a === "string" ? { value: a } : { literal: a })) });

/**
 * 停住点：物体停在哪，写取值的名字或字面量（协议第 4.1 节第 4 项）。运行时按通道的停住点的调用调用；
 * 落点按力场去，当前值就地停住（结束运动，速度 0），其余是终态，由力场带过去。
 */
export const stopAt = (value: string | number | boolean): Output =>
  ({ tag: "停住点", value: typeof value === "string" ? { value } : { literal: value } });

interface DragOptions { cancel?: readonly Output[]; ignore?: readonly string[] }

/**
 * 按住时（按下、拖动）每个拖动组件都一样的三行：取消、输入丢失、明确忽略。
 * 指针离开响应区也是取消：出了区读不到手了，手放开，物体按取消输出或力场复位（用户 10-24「阻断后输入丢失不应该由于力场的工作自动复位吗」）。
 * 其余输入丢失保持，等校正出的左键松开（用户：「输入丢失不当做取消，输入丢失那就是事件」，由组件的行决定）。
 */
export function heldRows(from: readonly Mode[], opts: DragOptions = {}): Row[] {
  return [
    { from, on: ["pointer.right↑", "key.Escape↑", "loss.leftzone"], to: "静止", out: [CLAIM, ...(opts.cancel ?? [])],
      note: opts.cancel?.length ? "取消（右键、Esc、指针离开响应区）：回起点" : "取消（右键、Esc、指针离开响应区），不调用" },
    { from, on: LOSS_EVENTS.filter((e) => e !== "loss.leftzone"), to: "=", note: "保持，等校正出的左键松开" },
    { from, on: [...OTHER_BUTTONS, "key.Escape↓", "key.Escape⟳", "wheel~", "request.activate", "request.contextmenu", ...(opts.ignore ?? [])],
      to: "=", out: [CLAIM], note: "明确忽略：拖动中不滚页面、不弹菜单、不前进后退" },
  ];
}

/**
 * 没有按下模式的拖动（协议第 4.2 节“交给外层”）：按下时不进独占模式，内外层都还能看后面的位置变化；
 * 在组件上按住、离开拖动框（内层再加上沿运动方向）后，从静止直接进入拖动。
 * 公共部分：静止、拖动两个模式，拖动独占指针、左键必然按住、由手带；拖动中的取消、输入丢失、明确忽略三行。
 * 静止时的行（按下、松开、位置变化、上下文菜单）各组件自己写：外层收下，内层交给外层。
 */
export function dragFromRest(opts: DragOptions = {}) {
  return {
    modes: ["静止", "拖动"] as readonly Mode[],
    exclusive: ["拖动"] as readonly Mode[],
    held: { "拖动": ["pointer.left"] } as const,
    hand: ["拖动"] as readonly Mode[],
    rows: heldRows(["拖动"], opts),
  };
}

/**
 * 拖动组件的公共部分（协议第 4.1 节“拖动的公共行”）：静止、按下、拖动三个模式；按下和拖动独占指针、左键必然按住；
 * 拖动时由手带（协议第 4.5 节）。外加每个拖动组件都一样的六行。组件只写按下、点击、开始拖动、拖动中、松手这几行和自己的键。
 * cancel：取消（右键、Esc）时的输出，例如停住点是起点的值；ignore：拖动中另外明确忽略的输入，例如组件自己的方向键。
 */
export function dragBasics(opts: DragOptions = {}) {
  const rows: Row[] = [
    { from: ["静止"], on: ["pointer.left↓"], to: "=", note: "静止时的松开：在别处按下、按下后已取消，或双击的第二次松开" },
    { from: ["静止"], on: ["request.contextmenu"], to: "=", out: [CLAIM], note: "不弹浏览器菜单" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": false }, to: "=", note: "还在拖动框里" },
    ...heldRows(["按下", "拖动"], opts),
  ];
  return {
    modes: ["静止", "按下", "拖动"] as readonly Mode[],
    exclusive: ["按下", "拖动"] as readonly Mode[],
    held: { "按下": ["pointer.left"], "拖动": ["pointer.left"] } as const,
    hand: ["拖动"] as readonly Mode[],
    rows,
  };
}
