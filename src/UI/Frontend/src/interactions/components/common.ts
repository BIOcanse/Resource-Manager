// Shared input rows and outputs remain subject to transition-table checks.


import type { Call, Definition, Mode, Output, Row } from "../protocol/definition.ts";
import type { Value } from "../protocol/vec.ts";

/** Entry/exit animates presentation without moving hit regions. Plane motion transforms live nodes and their hit regions. */
export const MOTION_KINDS = ["进场退场", "平面移动"] as const;
export type MotionKind = (typeof MOTION_KINDS)[number];

/** Interaction definition with an explicit motion category. */
export type PartDefinition<V extends Value = number, K extends MotionKind = MotionKind> = Definition<V> & { readonly motion: K };

/** Input-loss events. */
export const LOSS_EVENTS = ["loss.blur", "loss.focus", "loss.hidden", "loss.visible", "loss.pointercancel", "loss.lostcapture", "loss.leftzone"] as const;

/** Other pointer-button changes while the left button remains held. */
export const OTHER_BUTTONS = [
  "pointer.right↓", "pointer.middle↑", "pointer.middle↓", "pointer.back↑", "pointer.back↓", "pointer.forward↑", "pointer.forward↓",
] as const;

export const CLAIM: Output = { tag: "框架", act: "认领" };
export const FOCUS: Output = { tag: "框架", act: "焦点" };
export const JUMP_TO_POINTER: Output = { tag: "动画", signal: "跳到指针处" };
/** Decline this row so the outer component can handle it. */
export const PASS: Output = { tag: "框架", act: "交给外层" };

/** Call arguments are value names or numeric/boolean literals. */
export const call = (port: string, ...args: readonly (string | number | boolean)[]): Call =>
  ({ tag: "业务", port, args: args.map((a) => (typeof a === "string" ? { value: a } : { literal: a })) });

/** Stop at a named value or literal. Landing uses the field; current stops immediately with zero velocity. */
export const stopAt = (value: string | number | boolean): Output =>
  ({ tag: "停住点", value: typeof value === "string" ? { value } : { literal: value } });

interface DragOptions {
  cancel?: readonly Output[];
  ignore?: readonly string[];
  /** Input losses treated as cancellation. Other losses wait for a corrected release. */
  cancelOnLoss?: readonly (typeof LOSS_EVENTS)[number][];
}

/** Shared held-state rows: cancel outside the response area, handle input loss, and explicitly ignore inputs. */
export function heldRows(from: readonly Mode[], opts: DragOptions = {}): Row[] {
  const lossCancels = (opts.cancelOnLoss ?? []).filter((e) => e !== "loss.leftzone");
  const kept = LOSS_EVENTS.filter((e) => e !== "loss.leftzone" && !lossCancels.includes(e));
  const what = `右键、Esc、指针离开响应区${lossCancels.length ? `、${lossCancels.join("、")}` : ""}`;
  return [
    { from, on: ["pointer.right↑", "key.Escape↑", "loss.leftzone", ...lossCancels], to: "静止", out: [CLAIM, ...(opts.cancel ?? [])],
      note: opts.cancel?.length ? `取消（${what}）：回起点` : `取消（${what}），不调用` },
    ...(kept.length ? [{ from, on: kept, to: "=", note: "保持，等校正出的左键松开" } satisfies Row] : []),
    { from, on: [...OTHER_BUTTONS, "key.Escape↓", "key.Escape⟳", "wheel~", "request.activate", "request.contextmenu", ...(opts.ignore ?? [])],
      to: "=", out: [CLAIM], note: "明确忽略：拖动中不滚页面、不弹菜单、不前进后退" },
  ];
}

/** Drag without a pressed mode: both layers observe movement until the drag threshold is crossed. */
export function dragFromRest(opts: DragOptions = {}) {
  return {
    modes: ["静止", "拖动"] as readonly Mode[],
    exclusive: ["拖动"] as readonly Mode[],
    held: { "拖动": ["pointer.left"] } as const,
    hand: ["拖动"] as readonly Mode[],
    rows: heldRows(["拖动"], opts),
  };
}

/** Shared idle/pressed/dragging modes. Held modes own the pointer; dragging follows the hand. */
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
