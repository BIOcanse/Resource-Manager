import { onCleanup, untrack } from "solid-js";
import { entranceDefinitions } from "../interactions/components/entrance.ts";
import type { EntrancePart } from "../interactions/components/entrance.ts";
import type { MotionKind } from "../interactions/components/common.ts";
import type { Instance, Presentation } from "../interactions/protocol/runtime.ts";
import { interactionAnimationMode, interactionRuntime } from "../interactions/solid/lodestone.ts";
import { createOrderHost } from "../interactions/solid/orderHost.ts";

/*
 * 进场、退场的呈现层（docs/animation_style_2026-10-26.md「总原则：交互与无动画完全一致」）。
 *
 * DOM 是交互的唯一真相：组件挂上、卸下，焦点、inert、输入路由和命中几何都在事件发生时就改成终态，和无动画一样。
 * 进场、退场只改画面，由这里一处持有；“进场中”“退出中”只存在于这里，组件不各自保存影响交互的动画状态。
 * 以后换 UnboundWebView，这里就是改用框架动画元数据的唯一位置，别处不依赖退场幽灵的 DOM。
 *
 * 退场：节点在交互上已经离开（卸下，或者原地换成了别的内容）。这里在它原来的父元素里放一份只能看见的幽灵：
 * 不可命中（pointer-events: none）、inert、aria-hidden，没有 id、role、tabindex 和身份类的 data 属性。
 * 幽灵播放这个节点默认的退出动画槽位（styles/motion.css 里 [data-exit-ghost] 的规则，按节点自己的 class，
 * 或调用方给的槽位名），播完撤掉。无动画、超高性能的槽位是空的：不放幽灵，退出当场结束。
 * 幽灵在下一帧画出来之前追加在父元素末尾，画在原处同层内容的上面；它按原来的外框绝对定位，不占文档流。
 *
 * 进场：真实节点从第一帧起就在终态几何上，可点、可聚焦、在可访问性树里。进场只改不透明度（不透明度 0 的节点照样可命中），
 * 不画位移和缩放：现有宿主里变换会带着命中区域一起移动。带位移的效果只放在退场幽灵上。
 * 磁石带的进场（弹窗、换页一类，enterNode）每次都是一个新的部件，从开度 0、静止开始，不接旧节点的速度；
 * CSS 带的进场（enterSlot）按 [data-enter-slot] 的规则播放。
 */

/**
 * 呈现层的槽位（styles/motion.css 按名字给画面）和它们的动画类别（components/common.ts 的 MotionKind）：这里只有进场退场，
 * 写成别的类别编译不过。进场槽位写在真实节点的 data-enter-slot，只改不透明度；退出槽位写在幽灵的 data-exit-slot。
 * 没写槽位名的退场按节点自己的 class 取 motion.css 里 [data-exit-ghost] 的默认槽位，同样只在幽灵上。
 * 磁石带的进场（enterNode）用的定义自己声明类别（components/entrance.ts，也是进场退场）。
 * 合同测试（tests/nodeAnimationContract.test.ts）核对这两张表和 motion.css 里用到的槽位名一一对应。
 */
type EntranceOrExit = Extract<MotionKind, "进场退场">;
export const ENTER_SLOTS = { row: "进场退场", disclosure: "进场退场" } as const satisfies Readonly<Record<string, EntranceOrExit>>;
export const EXIT_SLOTS = { content: "进场退场", disclosure: "进场退场" } as const satisfies Readonly<Record<string, EntranceOrExit>>;
export type EnterSlot = keyof typeof ENTER_SLOTS;
export type ExitSlot = keyof typeof EXIT_SLOTS;

/** 一份幽灵最多复制、清理多少个节点：超过就不放幽灵（退出当场结束）。 */
const GHOST_NODE_LIMIT = 20000;
/** 幽灵最长留多久：槽位动画没有按时结束（例如被父元素整个清空）也一定撤掉。 */
const GHOST_LIFETIME_MS = 3000;
/** 找仍在文档里的祖先时最多往上走几层。 */
const ANCESTOR_LIMIT = 64;
/**
 * 幽灵上保留的 data 属性：styles 里按它们选样式（合同测试 tests/nodeAnimationContract.test.ts 核对）。
 * 其余 data 属性都是身份（行、字段、焦点、可见区域），幽灵不能被当成真实节点找到，一律去掉。
 */
export const GHOST_STYLE_DATA_ATTRIBUTES: ReadonlySet<string> = new Set([
  "data-column-id", "data-tone", "data-bar-color", "data-placement", "data-form-factor", "data-exit-slot"
]);
const GHOST_REMOVED_ATTRIBUTES = ["id", "role", "tabindex", "name", "for", "autofocus", "contenteditable", "href"];

/** 槽位是不是空的：无动画、超高性能没有动画（动画风格目录，磁石的动画档位不是 normal）。 */
function slotsAreEmpty(): boolean {
  return untrack(interactionAnimationMode) !== "normal";
}

export interface ExitOptions {
  /** 退出动画槽位名（写到幽灵的 data-exit-slot，EXIT_SLOTS 里的一个）；不给就按节点自己的 class 取默认槽位。 */
  readonly slot?: ExitSlot;
  /** 槽位要的量，写成幽灵上的 CSS 变量（例如内容退出的位移方向）。 */
  readonly vars?: Readonly<Record<string, string>>;
  /**
   * 幽灵放在哪个定位元素里（按它的坐标放）；不给就放回原来的父元素。
   * 原来的父元素会马上滚动或换位置时用（例如换页时整页的滚动区回到顶端，旧页的幽灵要留在原处）。
   */
  readonly layer?: HTMLElement;
}

interface Placement {
  readonly parents: readonly HTMLElement[];
  readonly box: { left: number; top: number; width: number; height: number } | null;
  readonly scrolls: readonly { path: readonly number[]; left: number; top: number }[];
}

/** 节点此刻在哪：父元素链（最近的在前）、要不要按外框绝对定位、里面滚过的元素。量不到（不在文档里、不显示）给 null。 */
function measure(element: HTMLElement, layer?: HTMLElement): Placement | null {
  if (!element.isConnected || !element.parentElement) return null;
  const style = getComputedStyle(element);
  if (style.display === "none" || style.display === "contents") return null;
  if (layer) {
    const rect = element.getBoundingClientRect();
    const origin = layer.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return null;
    return {
      parents: [layer],
      box: { left: rect.left - origin.left - layer.clientLeft + layer.scrollLeft, top: rect.top - origin.top - layer.clientTop + layer.scrollTop, width: rect.width, height: rect.height },
      scrolls: scrolledInside(element)
    };
  }
  const parents: HTMLElement[] = [];
  let ancestor: HTMLElement | null = element.parentElement;
  while (ancestor && parents.length < ANCESTOR_LIMIT) {
    parents.push(ancestor);
    ancestor = ancestor.parentElement;
  }
  let box: Placement["box"] = null;
  if (style.position !== "absolute" && style.position !== "fixed") {
    const rect = element.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return null;
    const base = element.offsetParent instanceof HTMLElement && element.offsetParent !== document.body ? element.offsetParent : null;
    const origin = base ? base.getBoundingClientRect() : { left: -window.scrollX, top: -window.scrollY };
    box = {
      left: rect.left - origin.left - (base?.clientLeft ?? 0) + (base?.scrollLeft ?? 0),
      top: rect.top - origin.top - (base?.clientTop ?? 0) + (base?.scrollTop ?? 0),
      width: rect.width,
      height: rect.height
    };
  }
  return { parents, box, scrolls: scrolledInside(element) };
}

/** 节点里滚过的元素（按子节点序号的路径记）：节点挪进幽灵或复制后滚动位置会丢，放好以后按路径还原。 */
function scrolledInside(root: HTMLElement): { path: number[]; left: number; top: number }[] {
  const found: { path: number[]; left: number; top: number }[] = [];
  const stack: { element: Element; path: number[] }[] = [{ element: root, path: [] }];
  let visited = 0;
  while (stack.length > 0 && visited < GHOST_NODE_LIMIT) {
    const { element, path } = stack.pop()!;
    visited += 1;
    if (element.scrollTop !== 0 || element.scrollLeft !== 0) found.push({ path, left: element.scrollLeft, top: element.scrollTop });
    for (let index = 0; index < element.children.length; index += 1) stack.push({ element: element.children[index], path: [...path, index] });
  }
  return found;
}

function atPath(root: Element, path: readonly number[]): Element | null {
  let element: Element | null = root;
  for (const index of path) {
    element = element?.children[index] ?? null;
  }
  return element;
}

/** 幽灵的外观：去掉身份和可交互的属性（循环加节点上限，不递归）。超过上限给 false。 */
function stripIdentity(ghost: HTMLElement): boolean {
  const walker = document.createTreeWalker(ghost, NodeFilter.SHOW_ELEMENT);
  let visited = 0;
  for (let node: Node | null = ghost; node; node = walker.nextNode()) {
    visited += 1;
    if (visited > GHOST_NODE_LIMIT) return false;
    const element = node as Element;
    for (const name of GHOST_REMOVED_ATTRIBUTES) element.removeAttribute(name);
    const names = element.getAttributeNames();
    for (const name of names) {
      if ((name.startsWith("aria-") && name !== "aria-hidden") || (name.startsWith("data-") && !GHOST_STYLE_DATA_ATTRIBUTES.has(name))) {
        element.removeAttribute(name);
      }
    }
  }
  return true;
}

/** 复制出的幽灵补上 cloneNode 不带的样子：画布的内容、表单控件的当前值。两棵树结构相同，逐个对着走。 */
function copyLook(source: HTMLElement, copy: HTMLElement): void {
  const from = document.createTreeWalker(source, NodeFilter.SHOW_ELEMENT);
  const to = document.createTreeWalker(copy, NodeFilter.SHOW_ELEMENT);
  let visited = 0;
  for (let a: Node | null = source, b: Node | null = copy; a && b && visited < GHOST_NODE_LIMIT; a = from.nextNode(), b = to.nextNode()) {
    visited += 1;
    if (a instanceof HTMLCanvasElement && b instanceof HTMLCanvasElement && a.width > 0 && a.height > 0) {
      b.getContext("2d")?.drawImage(a, 0, 0);
    } else if ((a instanceof HTMLInputElement && b instanceof HTMLInputElement)) {
      b.value = a.value;
      b.checked = a.checked;
    } else if (a instanceof HTMLTextAreaElement && b instanceof HTMLTextAreaElement) {
      b.value = a.value;
    } else if (a instanceof HTMLSelectElement && b instanceof HTMLSelectElement) {
      b.selectedIndex = a.selectedIndex;
    }
  }
}

/**
 * 把幽灵放到原来的父元素里（父元素已不在文档里就往上找仍在的祖先），播放槽位，播完撤掉。
 * 放幽灵在下一帧的 requestAnimationFrame 里做（这一帧画出来之前）：去掉身份、挂上、还原滚动、取动画都要动整个幽灵
 * （换页时是整页），不压在输入的任务里，交互的终态不等它。画面上没有差别：旧节点在交互上早已离开，幽灵第一次画出来时就在原处、
 * 滚动已还原、槽位已开始。界面看不见时（后台）没有画面可画，不放幽灵。
 */
function place(ghost: HTMLElement, placement: Placement, options: ExitOptions): void {
  if (document.visibilityState === "hidden") return;
  requestAnimationFrame(() => {
    // 到这一帧时节点又被挂回文档（还在用）：不是离开，不放幽灵。
    if (ghost.isConnected) return;
    const parent = placement.parents.find((element) => element.isConnected);
    if (!parent || !stripIdentity(ghost)) return;
    ghost.setAttribute("data-exit-ghost", "");
    if (options.slot) ghost.setAttribute("data-exit-slot", options.slot);
    ghost.setAttribute("aria-hidden", "true");
    ghost.inert = true;
    ghost.style.pointerEvents = "none";
    if (placement.box) {
      Object.assign(ghost.style, {
        position: "absolute",
        left: `${placement.box.left}px`,
        top: `${placement.box.top}px`,
        width: `${placement.box.width}px`,
        height: `${placement.box.height}px`,
        margin: "0",
        boxSizing: "border-box"
      });
    }
    for (const [name, value] of Object.entries(options.vars ?? {})) ghost.style.setProperty(name, value);
    parent.append(ghost);
    for (const scroll of placement.scrolls) {
      const element = atPath(ghost, scroll.path);
      if (element) {
        element.scrollLeft = scroll.left;
        element.scrollTop = scroll.top;
      }
    }
    // 只等会结束的动画（退出槽位）；副本里循环播放的动画（例如读取中的转圈）不算。
    const animations = finiteAnimations(ghost);
    // 槽位是空的（这个节点没有退出动画）：退出当场结束。
    if (animations.length === 0) {
      ghost.remove();
      return;
    }
    const timer = window.setTimeout(() => ghost.remove(), GHOST_LIFETIME_MS);
    void Promise.allSettled(animations.map((animation) => animation.finished)).then(() => {
      window.clearTimeout(timer);
      ghost.remove();
    });
  });
}

/** 节点和它里面会结束的动画（循环播放的不算）。 */
function finiteAnimations(root: HTMLElement): Animation[] {
  return root.getAnimations({ subtree: true }).filter((animation) => animation.effect?.getComputedTiming().endTime !== Infinity);
}

/** 这一刻整块离开的内容（exitInPlace 记下，同一个任务的微任务里清掉）：里面卸下的节点由这块的幽灵带着。 */
const leavingRoots = new Set<HTMLElement>();

/**
 * 节点卸下时播放它的退出动画槽位：在组件建立时调用（例如在 ref 里），卸下时（Solid 清理，节点还在文档里）量好位置，
 * Solid 把它移出文档以后，把这个节点本身放回原处当幽灵（画布、滚动位置都在）。
 */
export function exitOnUnmount(element: HTMLElement, options: ExitOptions = {}): void {
  onCleanup(() => {
    if (slotsAreEmpty()) return;
    // 外面整块内容正在离开（换页、换分区）：这个节点随那块的幽灵一起走，不另放幽灵，也不必再量。
    for (const root of leavingRoots) if (root.contains(element)) return;
    stopEntrancesWithin(element);
    const placement = measure(element, options.layer);
    if (!placement) return;
    queueMicrotask(() => {
      // 清理后节点没有被移出文档（不是真的卸下）：不放幽灵。
      if (element.isConnected) return;
      place(element, placement, options);
    });
  });
}

/**
 * 节点原地换成了别的内容（例如虚拟表格的行位换了行、内容层换了内容）：在换之前调用，按此刻的样子复制一份幽灵。
 * keep 为 children 时只复制外壳，里面放这一刻的子节点本身（换内容时 Solid 会把它们移出文档；还在文档里的不挪）。
 */
export function exitInPlace(element: HTMLElement, options: ExitOptions & { readonly keep?: "copy" | "children" } = {}): void {
  if (slotsAreEmpty()) return;
  stopEntrancesWithin(element);
  const placement = measure(element, options.layer);
  if (!placement) return;
  leavingRoots.add(element);
  queueMicrotask(() => leavingRoots.delete(element));
  if (options.keep === "children") {
    const shell = element.cloneNode(false) as HTMLElement;
    const nodes = [...element.childNodes];
    queueMicrotask(() => {
      shell.append(...nodes.filter((node) => !node.isConnected));
      if (shell.childNodes.length > 0) place(shell, placement, options);
    });
    return;
  }
  const copy = element.cloneNode(true) as HTMLElement;
  copyLook(element, copy);
  place(copy, placement, options);
}

// ---- 磁石带的进场 ----

interface Entrance {
  readonly element: HTMLElement;
  readonly instance: Instance;
  readonly faded: readonly HTMLElement[];
}

/** 正在进场的节点：进场中只存在这里。 */
const entrances = new Map<HTMLElement, Entrance>();

function clearLook(elements: readonly HTMLElement[]): void {
  for (const element of elements) {
    element.style.removeProperty("opacity");
    element.style.removeProperty("will-change");
  }
}

function finishEntrance(entrance: Entrance): void {
  if (entrances.get(entrance.element) !== entrance) return;
  entrances.delete(entrance.element);
  clearLook(entrance.faded);
  interactionRuntime().remove(entrance.instance, performance.now());
}

/** 节点退出时：它和它里面正在进场的节点不再画（幽灵从这一刻的样子开始退出）。 */
function stopEntrancesWithin(root: HTMLElement): void {
  for (const entrance of [...entrances.values()]) {
    if (root.contains(entrance.element)) {
      entrances.delete(entrance.element);
      interactionRuntime().remove(entrance.instance, performance.now());
    }
  }
}

/**
 * 节点进场：节点已经挂上、在终态几何上可交互，这里只让它（和 alsoFade 里的元素）的不透明度由磁石从 0 带到 1。
 * 同一个节点上一次进场还没走完就再进场（例如内容层原地换了内容）：上一次直接结束，新的从 0、静止开始。
 * 无动画、超高性能：不画，节点当场就是终态。
 */
export function enterNode(element: HTMLElement, options: { readonly part: EntrancePart; readonly name: string; readonly alsoFade?: readonly HTMLElement[] }): void {
  const running = entrances.get(element);
  if (running) finishEntrance(running);
  if (slotsAreEmpty()) return;
  const faded = [element, ...(options.alsoFade ?? [])];
  const host = createOrderHost(0);
  let entrance: Entrance | null = null;
  const draw = (x: number) => {
    if (!entrance || entrances.get(element) !== entrance) return;
    // 节点已经不在文档里（卸下了）：不再画。
    if (!element.isConnected) {
      const done = entrance;
      queueMicrotask(() => finishEntrance(done));
      return;
    }
    const p = Math.min(1, Math.max(0, x));
    if (p >= 1) {
      const done = entrance;
      clearLook(faded);
      queueMicrotask(() => finishEntrance(done));
      return;
    }
    for (const target of faded) {
      target.style.willChange = "opacity";
      target.style.opacity = p.toFixed(4);
    }
  };
  const view: Presentation = {
    element,
    bindingLabel: "不透明度",
    geometry: () => null,
    render: (d) => draw(d.x as number),
    aria: () => ""
  };
  const instance = interactionRuntime().add(options.name, entranceDefinitions[options.part], view, host.host);
  entrance = { element, instance, faded };
  entrances.set(element, entrance);
  draw(0);
  host.set(1, "节点进场");
}

// ---- CSS 带的进场 ----

/**
 * 节点进场，画面按 [data-enter-slot] 的 CSS 规则播放（只改不透明度），播完去掉属性。
 * 无动画、超高性能：不加属性，节点当场就是终态。
 */
export function enterSlot(element: HTMLElement, slot: EnterSlot): void {
  if (slotsAreEmpty()) return;
  element.setAttribute("data-enter-slot", slot);
  // 取动画要算样式：放到下一帧画出来之前，不在输入的任务里强制算。
  requestAnimationFrame(() => {
    if (element.getAttribute("data-enter-slot") !== slot) return;
    const animations = finiteAnimations(element);
    if (animations.length === 0) {
      element.removeAttribute("data-enter-slot");
      return;
    }
    void Promise.allSettled(animations.map((animation) => animation.finished)).then(() => {
      if (element.getAttribute("data-enter-slot") === slot) element.removeAttribute("data-enter-slot");
    });
  });
}

/** root 里正在播的 CSS 进场直接到终态（例如虚拟表格滚动，行位换成了别的行）。 */
export function settleEntrancesWithin(root: HTMLElement): void {
  for (const element of root.querySelectorAll("[data-enter-slot]")) element.removeAttribute("data-enter-slot");
}
