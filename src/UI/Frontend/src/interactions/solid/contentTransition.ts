import { createComputed, createEffect, on, onCleanup, untrack } from "solid-js";
import type { Accessor } from "solid-js";
import type { Presentation } from "../protocol/runtime.ts";
import { contentTransitionDefinition } from "../components/contentTransition.ts";
import { interactionAnimationMode, interactionRuntime, useInteraction } from "./lodestone.ts";
import { createOrderHost } from "./orderHost.ts";

/*
 * 内容过渡的呈现（定义在 ../components/contentTransition.ts）。docs/page_motion_2026-10-25.md 第 3 节。
 *
 * 显示的内容（key）换了：
 * 1. 换之前（Solid 改 DOM 之前，createComputed）：记下旧内容在层里的框；旧内容就地换（clone）时先复制一份静态副本。
 * 2. 换之后（createEffect）：把旧内容放进层里原来的位置，作为退出副本——整页换掉（move）时就是被 Solid 卸下的那些节点，
 *    就地换时是第 1 步的副本。副本不可交互（inert）、读屏看不见、没有 id。然后告诉运行时终态是新内容的序号。
 * 3. 每一帧按这一段走了多少（p，0 到 1）画：新内容从序号增大的一侧移进来、淡入；退出副本往反方向移出去一小段、淡出。
 *    p 是缓动后的进度，减速曲线前段走得快（标准曲线 11% 的时间就到 0.6），退出按 p 线性淡出就只剩两帧，像闪了一下：
 *    所以退出副本的不透明度是 1 − p³（标准手感约 100 ms 降到一成多），位移只走距离的三分之一。新内容画在副本上面。
 *    走完时撤掉退出副本、清掉新内容上的样式（不留 transform 和 opacity，免得改变里面固定定位的元素）。
 * 走到一半又换：正在进来的内容按它此刻的不透明度和位移变成退出副本，已有的退出副本从此刻的样子接着出去，不跳。
 * 动画档位不是正常时运行时直接到终态，第 2 步的最后一画就是 p = 1：旧内容立刻撤掉、新内容立刻在位。
 */

export type ContentSnapshot = "move" | "clone";

export interface ContentTransitionOptions {
  readonly name: string;
  /** 现在显示的是哪一份内容。 */
  readonly key: Accessor<string>;
  /** 这份内容的序号（分页树或列表里的次序）：方向和进度都按它。 */
  readonly order: Accessor<number>;
  /** 显示当前内容的元素（新内容画在它上面）。可以随 key 换成另一个元素（例如另一个页签面板）。 */
  readonly stage: () => HTMLElement | undefined;
  /** 放退出副本的层：定位元素，不随内容滚动；副本按它的坐标放。 */
  readonly layer: () => HTMLElement | undefined;
  /** 旧内容怎么离开：move 用 Solid 卸下的旧节点（整份内容被换掉），clone 用换之前复制的副本（内容就地换）。 */
  readonly snapshot: Accessor<ContentSnapshot>;
  /** 进出的方向和距离（px）。不写是横向 24 px。 */
  readonly axis?: "x" | "y";
  readonly distance?: number;
}

interface Leaving { readonly element: HTMLElement; opacity: number; shift: number }

/** 退出副本的不透明度 = 1 − p^LEAVE_FADE_POWER（见上面第 3 步）。 */
const LEAVE_FADE_POWER = 3;
/** 退出副本的位移是进来那段距离的几分之几。 */
const LEAVE_SHIFT = 1 / 3;
/** 退出副本最多留几份：连着换得很快时，最早的那份已经几乎看不见，直接撤掉。 */
const LEAVING_LIMIT = 3;

export function useContentTransition(options: ContentTransitionOptions): void {
  const axis = options.axis ?? "x";
  const distance = options.distance ?? 24;
  const orderHost = createOrderHost(untrack(options.order));
  let fromOrder = untrack(options.order);
  let toOrder = fromOrder;
  let direction = 1;
  let drawnX = fromOrder;
  /** 正在画的新内容和它此刻的样子。 */
  let stage: HTMLElement | undefined;
  let stageLook = { opacity: 1, shift: 0 };
  let leaving: Leaving[] = [];
  let pending: { mode: ContentSnapshot; box: { left: number; top: number; width: number }; nodes: Node[]; shell: HTMLElement } | null = null;

  const translate = (shift: number) => (axis === "x" ? `translateX(${shift}px)` : `translateY(${shift}px)`);

  const settle = () => {
    for (const item of leaving) item.element.remove();
    leaving = [];
    if (stage) {
      stage.style.removeProperty("opacity");
      stage.style.removeProperty("transform");
      stage.style.removeProperty("will-change");
    }
    stageLook = { opacity: 1, shift: 0 };
  };

  const draw = (x: number) => {
    drawnX = x;
    const span = toOrder - fromOrder;
    const p = span === 0 ? 1 : Math.min(1, Math.max(0, (x - fromOrder) / span));
    if (p >= 1) {
      settle();
      return;
    }
    if (stage) {
      stageLook = { opacity: p, shift: direction * (1 - p) * distance };
      stage.style.willChange = "transform, opacity";
      stage.style.opacity = String(stageLook.opacity);
      stage.style.transform = translate(stageLook.shift);
    }
    const fade = 1 - p ** LEAVE_FADE_POWER;
    for (const item of leaving) {
      item.element.style.opacity = String(item.opacity * fade);
      item.element.style.transform = translate(item.shift - direction * p * distance * LEAVE_SHIFT);
    }
  };

  const view = (): Presentation => ({
    element: options.layer(),
    bindingLabel: "新内容和退出副本的位移、不透明度",
    geometry: () => null,
    render: (d) => draw(d.x as number),
    aria: () => ""
  });
  const handle = useInteraction(options.name, contentTransitionDefinition, view, orderHost.host);
  onCleanup(settle);

  // 1. 换之前：旧内容还在原处。
  createComputed(on(options.key, () => {
    pending = null;
    // 无动画档没有退出副本，不读取旧几何，也不克隆即刻丢弃的节点。
    if (untrack(interactionAnimationMode) !== "normal") return;
    const current = stage ?? untrack(options.stage);
    const layer = untrack(options.layer);
    if (!handle.instance || !current || !layer || !current.isConnected) return;
    const mode = untrack(options.snapshot);
    const base = layer.getBoundingClientRect();
    const rect = current.getBoundingClientRect();
    const nodes = [...current.childNodes];
    pending = {
      mode,
      box: {
        left: rect.left - base.left - layer.clientLeft + layer.scrollLeft,
        top: rect.top - base.top - layer.clientTop + layer.scrollTop,
        width: rect.width
      },
      nodes: mode === "clone" ? nodes.map((node) => node.cloneNode(true)) : nodes,
      shell: current.cloneNode(false) as HTMLElement
    };
  }, { defer: true }));

  // 2. 换之后：旧内容放进层里，新内容从这一刻开始进来。
  createEffect(on(options.key, () => {
    const instance = handle.instance;
    const next = options.stage();
    const layer = options.layer();
    const snapshot = pending;
    pending = null;
    // 已有的退出副本：从此刻的样子接着出去。
    const span = toOrder - fromOrder;
    const p = span === 0 ? 1 : Math.min(1, Math.max(0, (drawnX - fromOrder) / span));
    const fade = 1 - p ** LEAVE_FADE_POWER;
    for (const item of leaving) {
      item.opacity *= fade;
      item.shift -= direction * p * distance * LEAVE_SHIFT;
    }
    if (snapshot && layer) {
      const shell = snapshot.shell;
      const nodes = snapshot.mode === "move" ? snapshot.nodes.filter((node) => !node.isConnected) : snapshot.nodes;
      shell.append(...nodes);
      shell.removeAttribute("id");
      for (const element of shell.querySelectorAll("[id]")) element.removeAttribute("id");
      shell.inert = true;
      shell.setAttribute("aria-hidden", "true");
      shell.classList.add("content-transition-leaving");
      Object.assign(shell.style, {
        position: "absolute",
        left: `${snapshot.box.left}px`,
        top: `${snapshot.box.top}px`,
        width: `${snapshot.box.width}px`,
        margin: "0",
        pointerEvents: "none",
        opacity: String(stageLook.opacity),
        transform: translate(stageLook.shift),
        willChange: "transform, opacity"
      });
      layer.prepend(shell);
      leaving.push({ element: shell, opacity: stageLook.opacity, shift: stageLook.shift });
      while (leaving.length > LEAVING_LIMIT) leaving.shift()!.element.remove();
    }
    if (stage && stage !== next) {
      stage.style.removeProperty("opacity");
      stage.style.removeProperty("transform");
      stage.style.removeProperty("will-change");
    }
    stage = next;
    stageLook = { opacity: 0, shift: 0 };
    const target = options.order();
    fromOrder = drawnX;
    toOrder = target;
    direction = target >= drawnX ? 1 : -1;
    if (!instance) {
      drawnX = target;
      fromOrder = target;
      settle();
      return;
    }
    orderHost.set(target, "显示的内容换了");
    draw(interactionRuntime().driveOf(instance, performance.now()).x as number);
  }, { defer: true }));

  // 第一次挂上时记下当前的元素。
  createEffect(() => {
    if (!stage) stage = options.stage();
  });
}
