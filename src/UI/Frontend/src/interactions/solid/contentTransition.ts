import { createComputed, createEffect, on, untrack } from "solid-js";
import type { Accessor } from "solid-js";
import { enterNode, exitInPlace } from "../../presentation/nodeAnimation.ts";

/*
 * 内容过渡：换页、切二级分页、对话框里的页签、设置的分区共用（docs/animation_style_2026-10-26.md「总原则」）。
 *
 * 显示的内容（key）换了，交互上当场就是新内容：Solid 换掉 DOM，新内容在终态几何上可点、可聚焦，和无动画一样。
 * 画面交给呈现层（presentation/nodeAnimation.ts）：
 * 1. 换之前（Solid 改 DOM 之前，createComputed）：旧内容留一份幽灵，播放内容的退出槽位：淡出，并往和新内容相反的方向移出一小段
 *    （幽灵不可命中，位移不影响交互）。整页换掉（move）时幽灵里放的就是被 Solid 卸下的那些节点，就地换（clone）时是这一刻的副本。
 * 2. 换之后（createEffect）：新内容进场，只改不透明度，由磁石从 0、静止开始带到 1（不接旧内容的速度，不等旧内容的幽灵）。
 * 方向只决定幽灵往哪边退：序号变大，旧内容往前一侧（负方向）退。无动画、超高性能：没有幽灵，新内容当场在位。
 */

export type ContentSnapshot = "move" | "clone";

export interface ContentTransitionOptions {
  readonly name: string;
  /** 现在显示的是哪一份内容。 */
  readonly key: Accessor<string>;
  /** 这份内容的序号（分页树或列表里的次序）：只用来定旧内容退出的方向。 */
  readonly order: Accessor<number>;
  /** 显示当前内容的元素。可以随 key 换成另一个元素（例如另一个页签面板）。 */
  readonly stage: () => HTMLElement | undefined;
  /** 旧内容的幽灵放在哪个定位元素里；不写就放在显示内容的元素旁边（它的父元素里）。 */
  readonly layer?: () => HTMLElement | undefined;
  /** 旧内容怎么离开：move 用 Solid 卸下的旧节点（整份内容被换掉），clone 用换之前复制的副本（内容就地换）。 */
  readonly snapshot: Accessor<ContentSnapshot>;
  /** 旧内容退出的方向和距离（px）。不写是横向 8 px。 */
  readonly axis?: "x" | "y";
  readonly distance?: number;
}

export function useContentTransition(options: ContentTransitionOptions): void {
  const axis = options.axis ?? "x";
  const distance = options.distance ?? 8;
  let shownOrder = untrack(options.order);

  // 1. 换之前：旧内容还在原处，留幽灵。
  createComputed(on(options.key, () => {
    const current = untrack(options.stage);
    if (!current) return;
    const next = untrack(options.order);
    const shift = `${next >= shownOrder ? -distance : distance}px`;
    exitInPlace(current, {
      slot: "content",
      keep: untrack(options.snapshot) === "move" ? "children" : "copy",
      layer: options.layer ? untrack(options.layer) : undefined,
      vars: { "--exit-shift-x": axis === "x" ? shift : "0px", "--exit-shift-y": axis === "y" ? shift : "0px" }
    });
  }, { defer: true }));

  // 2. 换之后：新内容进场。
  createEffect(on(options.key, () => {
    shownOrder = options.order();
    const next = options.stage();
    if (next) enterNode(next, { part: "page", name: options.name });
  }, { defer: true }));
}
