// 运行时：路由（谁收到这个输入变化、为什么）、按转移表求值、三类输出、通道的运动（手和力场）。
// 不碰 DOM：呈现层、宿主和平台操作通过接口注入，测试里用假的。
// 一段运动、几何这两项通过查询向构造要（协议第 4.7 节）：执行方式定下用原始还是优化的算法，或者对照。

import { CURRENT, declaredSpecs, LANDING, ORIGIN, readsOf, REST, STOP_POINT } from "./definition.ts";
import type { Arg, Call, Definition, FactContext, FactId, Geometry, Mode, Output, Row, ValueContext, ValueResult } from "./definition.ts";
import { queries } from "./optimizer.ts";
import type { Queries } from "./optimizer.ts";
import { REFERENCE } from "./plan.ts";
import type { Execution, Mismatch } from "./plan.ts";
import { FACTS } from "./facts.ts";
import { changeLabel, HOLD_REPEAT, InputLayer, LOSS_LABELS, POINTER_BUTTONS, POINTER_LEAVE } from "./input.ts";
import type { InputChange, LossId, RawInput } from "./input.ts";
import type { ParamId, ParamLayer, ParamOverrides, ParamSpec, ParamTable, SystemReadings } from "./params.ts";
import { cite, globalLayer, partLayer, resolveParams } from "./params.ts";
import { checkInstances } from "./check.ts";
import { keep } from "./field.ts";
import { fieldForce, frameOf, handFrom, handPosition, holdAt, isPlane, ms as msText, pointerValue, restPoint, stepValue } from "./physics.ts";
import type { FieldMotion, Force, Frame, Place } from "./physics.ts";
import { fmtValue as fmt, sameValue, scale, sub, valueOfVec, vec, ZERO } from "./vec.ts";
import type { Value } from "./vec.ts";
import { pushSample, valueAt, windowRate } from "./samples.ts";
import type { Trace } from "./trace.ts";

export type AnimationMode = "normal" | "none" | "ultra";
export const ANIMATION_LABELS: Readonly<Record<AnimationMode, string>> = { normal: "正常", none: "无动画", ultra: "极简" };

export interface Rect { left: number; top: number; right: number; bottom: number }

/** 这一刻谁在带物体（协议第 4.5 节）：手（按住时），力场，或者停住。 */
export type Driver = "手" | "力场" | "停住";

/**
 * 交给绑定的驱动量（协议第 3.3 节）。绑定按需映射到运动、形态和颜色；演示的叠加层也画它们。
 * V 是通道的值的样子：线是数，平面是点（协议第 4.5 节“平面”）。
 */
export interface Drive<V extends Value = number> {
  /** 通道的位置；有几何时就是逻辑中心的位置。 */
  x: V;
  /** 通道的速度，值单位/秒。 */
  v: V;
  /** 加速度，值单位/秒²：各项相加（手带着时按位置速度的变化率，手 = 加速度 − 其余各项）。 */
  a: V;
  /** 这一刻的各项加速度：手和力场（手绝对吸附、按住时），或力场。 */
  forces: readonly Force<V>[];
  /** 手带着时手的位置，否则 null。手在边界内时就是物体的位置。 */
  hand: V | null;
  driver: Driver;
  mode: Mode;
  /** 所在段：手带着时，此刻静止松手会停到的输出映射点；会停在别处或手不在时为 null。 */
  segment: V | null;
}

/** 呈现层：组件元素、绑定和几何。平台操作（捕获、焦点、包含关系、外框）不在这里，见 Platform。 */
export interface Presentation<V extends Value = number> {
  element: unknown;
  /** 通道绑定到什么，例如“位移”“宽度”“颜色”。 */
  bindingLabel: string;
  /** 通道能对应到屏幕位置时报告几何（值最小、最大时逻辑中心的屏幕位置），否则 null。 */
  geometry(): Geometry | null;
  /**
   * 组件元素里交给浏览器的原生控件（协议第 4.2 节第 2 项）：事件目标在这样的控件上时为 true，这次输入（指针、键盘、激活请求）交给浏览器，不进路由。
   * 只问包含事件目标的最内层组件。不写就是没有：组件自己的控件（例如开关的 checkbox）不算。
   */
  nativeControl?(target: unknown): boolean;
  /** 按驱动量画：有几何时把逻辑中心放在 x 对应的位置；部件自身的缩放以逻辑中心为基准。 */
  render(d: Drive<V>): void;
  /** 写无障碍状态，返回写了什么。 */
  aria(hostValue: V): string;
}

/** 平台操作：所有组件共用一份，按组件的元素做。网页里是 DOM，测试里用假的。 */
export interface Platform {
  /** 事件目标是否在元素内（包含关系）。 */
  contains(element: unknown, target: unknown): boolean;
  /** 元素在屏幕上的外框。 */
  rect(element: unknown): Rect | null;
  capture(element: unknown, pointerId: number): void;
  release(element: unknown, pointerId: number): void;
  focus(element: unknown): void;
  /** 响应区（协议第 2.1 节）：读指针的范围，视口 px。浏览器里是整个窗口。 */
  responseZone(): Rect;
  /**
   * 请一帧（协议第 4.1 节“帧按需”）：运行时要下一帧时调用，宿主在下一帧调用一次 frame(时刻)。两次 frame 之间最多调用一次。
   * 宿主本来每帧都调用 frame 时，这里什么也不用做。
   */
  requestFrame(): void;
}

/** 宿主：值的唯一持有者。组件只调用它的函数；宿主怎么处理是宿主的事。值的样子和通道一样（线是数，平面是点）。 */
export interface Host<V extends Value = number> {
  value(): V;
  /** 宿主让组件禁用时为 false：禁用的组件不参与路由，物体照常被带到宿主值（协议第 4.2 节）。 */
  enabled(): boolean;
  call(port: string, args: readonly unknown[], cause: string): void;
  /** 订阅宿主值的变化，返回退订（部件移除时运行时调用它）。 */
  subscribe(listener: (old: V, next: V, cause: string) => void): () => void;
}

/** 通道的状态（协议第 4.5 节）：物体的位置和速度，以及现在谁在带它。值的样子和通道一样（线是数，平面是点）。 */
interface ChannelState<V extends Value> {
  x: V;
  v: V;
  /** 进入独占模式时记下的位置（起点）。 */
  origin: V;
  /** 按下时手所在处：按在部件上是逻辑中心，写了“跳到指针处”是指针处。手的位置 = 它 + 指针的位移。 */
  handStart: V;
  /** 手带着时手的位置，否则 null。 */
  hand: V | null;
  lastT: number;
  /** 从进入独占模式起物体位置的采样，算手带着时的速度。 */
  samples: { t: number; x: V }[];
  /**
   * 力场带着物体时：这一段（放置点上一次变了以来）的运动、开始时刻、已画的帧数、开始时的几何换算（算力场这一项用，帧里不再读布局；没有几何时为 null）。
   * since 是物体这次被力场连续带着动起来的时刻：放置点变了、物体还在动时起的下一段不变，停下或被按住就结束（为 null）。
   */
  motion: { m: FieldMotion<V>; t0: number; frames: number; frame: Frame | null; since: number } | null;
  /** 这次手势（进入独占模式到离开）按下时读的几何和它的换算；不在手势里、按下时没有几何为 null。几何查询的优化算法用它（协议第 4.7 节）。 */
  gesture: { geometry: Geometry; frame: Frame } | null;
}

export class Instance<V extends Value = Value> {
  mode: Mode = REST;
  segment: V | null = null;
  readonly ch: ChannelState<V>;
  /** 这个部件这一层在配置区里覆盖的项（协议第 5 节）；改它走 Runtime.setOverride。 */
  readonly overrides: ParamOverrides = {};
  /** 这个实例级联后的完整参数表；它读的一切参数都来自这里。由运行时在层变化时重算。 */
  params!: ParamTable;

  readonly name: string;
  readonly def: Definition<V>;
  readonly view: Presentation<V>;
  readonly host: Host<V>;
  /** 通道是平面（两条轴）。 */
  readonly plane: boolean;
  /** 这种值的 0：线是 0，平面是 (0, 0)。 */
  readonly zero: V;
  /** 向构造要一段运动、几何（协议第 4.7 节），按运行时的执行方式。 */
  readonly q: Queries;

  constructor(name: string, def: Definition<V>, view: Presentation<V>, host: Host<V>, q: Queries) {
    this.name = name;
    this.def = def;
    this.view = view;
    this.host = host;
    this.q = q;
    this.plane = isPlane(def.channel);
    this.zero = valueOfVec(this.plane, ZERO) as V;
    const v = host.value();
    this.ch = { x: v, v: this.zero, origin: v, handStart: v, hand: null, lastT: 0, samples: [], motion: null, gesture: null };
  }

  /** 这个模式由手带。 */
  holds(mode: Mode = this.mode): boolean {
    return this.def.channel.hand.includes(mode);
  }

  exclusive(mode: Mode = this.mode): boolean {
    return this.def.exclusive.includes(mode);
  }

  /** 这个模式读不读这个变化：它的原语在这个模式的行里出现过就读；独占模式另外读全部指针类变化和输入丢失事件。 */
  reads(change: InputChange): boolean {
    const c = change.change;
    if (readsOf(this.def, this.mode).has(c.id)) return true;
    return this.exclusive() && ((c.id.startsWith("pointer.") && c.id !== HOLD_REPEAT) || c.kind === "event");
  }

  /** 几何（查询）：呈现层报告的几何，没有为 null。 */
  geometry(): Geometry | null {
    return this.q.geometry(() => this.view.geometry(), this.ch.gesture?.geometry ?? null);
  }

  /** 几何换算：有几何就有，否则 null。几何就是按下时读的那一份时，用按下时算好的换算。 */
  geometryFrame(): Frame | null {
    const g = this.geometry();
    if (!g) return null;
    return g === this.ch.gesture?.geometry ? this.ch.gesture.frame : frameOf(this.def.channel, g);
  }

  /** 这个通道的值：向量按线或平面换成值。 */
  value(v: { x: number; y: number }): V {
    return valueOfVec(this.plane, v) as V;
  }
}

/** 日志里的调用参数：值（数或点）照值写，其余照字面。 */
const argValue = (a: unknown) => (typeof a === "number" || (typeof a === "object" && a !== null && "x" in a) ? fmt(a as Value) : String(a));
const unitOf = (inst: Instance) => inst.def.channel.unit || "单位";
const tag = (inst: Instance) => `通道 ${inst.def.channel.label}`;
const jumps = (row: Row) => (row.out ?? []).some((o) => o.tag === "动画" && o.signal === "跳到指针处");
type StopOut = Extract<Output, { tag: "停住点" }>;
const stopOf = (row: Row) => (row.out ?? []).find((o): o is StopOut => o.tag === "停住点") ?? null;
const forceLine = (forces: readonly Force<Value>[], unit: string) =>
  (forces.length ? `各项加速度：${forces.map((f) => `${f.name} ${fmt(f.a)}`).join("、")} ${unit}/s²（相加 = 加速度）` : "各项加速度：无");

/**
 * 组合（协议第 4.2 节）：一组子组件和它们的区域路由。组合不是组件，没有模式，不读输入。
 * 宿主映射在宿主里，组合自己的画面在呈现层里，运行时只管区域路由。
 */
export interface Composite {
  name: string;
  element: unknown;
  children: readonly Instance[];
  /** 组合这一层在配置区里覆盖的项（协议第 5 节）：作用于它里面的部件。组合没有定义，没有定义写的值。 */
  readonly overrides: ParamOverrides;
  /** 区域路由：落在组合里、不在任何子组件上的指针类变化交给哪个子组件，以及理由；不交就返回 null。 */
  route(pos: { x: number; y: number }): { child: Instance; why: string } | null;
}

/** 部件（协议第 5 节）：页面上的一个组件实例或组合。参数按部件的嵌套一层层往里映射。 */
export type Part = Instance | Composite;

/** 配置区的一层：全局、某个母部件或本部件，从远到近排；来源不在这里（参数的 by 为 null 就是来源）。 */
export interface ConfigLayer extends ParamLayer {
  kind: "全局" | "母部件" | "本部件";
  /** 离本部件几层：本部件 0，母部件 1，母部件的母部件 2；全局为 null。 */
  distance: number | null;
  /** 这一层是哪个部件（全局为 null），配置区点它可以选中它。 */
  part: Part | null;
}

/** 配置区看一个部件（或全局）：从远到近的各层，和这个视角下的完整参数表（参数的 by.layer 是 layers 的下标）。 */
export interface ConfigView {
  /** 选中的部件；看全局时为 null。 */
  part: Part | null;
  layers: readonly ConfigLayer[];
  params: ParamTable;
}

export function rowText(def: Definition<Value>, row: Row): string {
  const index = def.rows.indexOf(row) + 1;
  const when = row.when ? Object.entries(row.when).map(([k, v]) => `${k}=${v ? "是" : "否"}`).join(" 且 ") : "—";
  const to = row.to === "=" ? "不变" : row.to;
  const out = row.out?.length ? row.out.map(outputText).join("；") : "无";
  return `#${index} ${row.from.join("、")} · ${row.on.join("、")} · ${when} → ${to} · ${out}`;
}

export function outputText(o: Output): string {
  if (o.tag === "业务") return `业务：${o.port}(${o.args.map(argText).join(", ")})`;
  if (o.tag === "停住点") return `停住点：${argText(o.value)}`;
  if (o.tag === "框架") return `框架：${o.act}`;
  return `动画：${o.signal}`;
}

const argText = (a: Arg) => ("value" in a ? a.value : String(a.literal));

export class Runtime {
  readonly input: InputLayer;
  readonly instances: Instance[] = [];
  private readonly composites: Composite[] = [];
  private system: SystemReadings | null;
  /** 全局层覆盖（范围最大的一层）。 */
  private readonly globalOverrides: ParamOverrides = {};
  private animation: AnimationMode = "normal";
  private holder: { inst: Instance; since: number; pointerId: number } | null = null;
  /**
   * 按住重复（协议第 2.7 节）：独占指针的组件进入读它的模式的时刻（since）、进的是哪个模式、下一次该来的时刻、已经来了几次。
   * 左键松开、不再独占、换到不读它的模式就为 null；在两个读它的模式之间换不重新算。
   */
  private holdRepeat: { inst: Instance; mode: Mode; since: number; next: number; n: number } | null = null;
  /** 上一次位置变化时事件目标所在的组件（指针离开用）。 */
  private hovered = new Set<Instance>();
  private lastPointerId = 1;
  /** 正在处理的输入变化的时间。处理期间宿主同步改值，用同一时刻，避免和事件时间戳混用两套时钟。 */
  private clock: number | null = null;
  /** 查过 checkInstances 以后，拖动框的参数改了就再查一次。 */
  private checked = false;
  /** 状态变了、下一帧要画的实例。输入和宿主变化只改状态、记在这里，画面一帧只写一次（事件里不写画面，读布局不会被迫重排）。 */
  private readonly toDraw = new Set<Instance>();
  /** 各实例对宿主的退订，移除时调用。 */
  private readonly unsubscribe = new Map<Instance, () => void>();
  /** 请过一帧、还没等到 frame（两次 frame 之间最多请一次）。 */
  private frameRequested = false;

  private readonly trace: Trace;
  private readonly platform: Platform;
  private readonly exec: Execution;

  /** exec：执行方式（协议第 4.7 节），不传就是原始执行。 */
  constructor(system: SystemReadings | null, trace: Trace, platform: Platform, exec: Execution = REFERENCE) {
    this.trace = trace;
    this.platform = platform;
    this.system = system;
    this.exec = exec;
    this.input = new InputLayer(() => platform.responseZone());
  }

  add<V extends Value>(name: string, def: Definition<V>, view: Presentation<V>, host: Host<V>): Instance<V> {
    const inst = new Instance<V>(name, def, view, host, queries(this.exec.mode, (m) => this.mismatch(name, m)));
    this.instances.push(inst as unknown as Instance);
    // 新加的部件可能是已有部件的母部件：全部重算
    this.resolveAll();
    this.unsubscribe.set(inst as unknown as Instance, host.subscribe((old, next, cause) => this.hostChanged(inst as Instance, old, next, cause)));
    view.aria(host.value());
    this.toDraw.add(inst);
    this.wake();
    return inst;
  }

  addComposite(c: Composite): void {
    this.composites.push(c);
    this.resolveAll();
  }

  /**
   * 移除部件（协议第 4.1 节“加入和移除”），元素卸载时调用。组件实例：不再路由、求值，不再调用它的呈现层和宿主，退订宿主；
   * 独占指针时手势直接结束（不求值、不调用），释放捕获；运动和等着的按住重复一起丢掉。组合：区域路由不再经过它。
   * 它可能是别的部件的母部件：全部重算。结束了什么就写一条“移除”；没加入过或已移除时什么也不做。
   */
  remove(part: Part, t: number): void {
    if (!(part instanceof Instance)) {
      const at = this.composites.indexOf(part);
      if (at < 0) return;
      this.composites.splice(at, 1);
      this.resolveAll();
      return;
    }
    const at = this.instances.indexOf(part);
    if (at < 0) return;
    const ended: string[] = [];
    const what: string[] = [];
    if (this.holder?.inst === part) {
      what.push("手势");
      ended.push(`独占指针中被移除（模式 ${part.mode}，自 #${this.holder.since} 起独占）：手势直接结束，不求值、不调用宿主`);
      this.platform.release(part.view.element, this.holder.pointerId);
      ended.push(`框架：释放指针（releasePointerCapture(${this.holder.pointerId})）`);
      this.holder = null;
    }
    if (this.holdRepeat?.inst === part) {
      what.push("按住重复");
      ended.push(`等着的按住重复丢掉（已来 ${this.holdRepeat.n} 次）`);
      this.holdRepeat = null;
    }
    this.advance(part, t);
    const was = this.moving(part);
    if (was) {
      what.push("运动");
      ended.push(`${tag(part)}：${was}中被移除，运动丢掉，停在 ${fmt(part.ch.x)}`);
    }
    part.ch.motion = null;
    this.instances.splice(at, 1);
    this.toDraw.delete(part);
    this.hovered.delete(part);
    this.unsubscribe.get(part)?.();
    this.unsubscribe.delete(part);
    this.resolveAll();
    if (what.length) this.trace.write({ t, kind: "移除", component: part.name, summary: `移除 ${part.name}：结束了${what.join("、")}`, lines: ended });
  }

  /**
   * 组件都加好后的检查（协议第 4.3 节第 10、11 项）：嵌套的拖动框、不同组件声明的同名参数。
   * 嵌套按元素的包含关系找：外层元素包含内层元素的每一对。结果写进日志（种类“检查”），也返回给页面显示。
   * 拖动框的参数改了以后再查一次（applyOverride）。
   */
  checkInstances(t: number): { ok: boolean; lines: string[] } {
    const nested: { outer: Instance; inner: Instance }[] = [];
    for (const outer of this.instances) for (const inner of this.instances) {
      if (outer !== inner && outer.view.element !== inner.view.element && this.platform.contains(outer.view.element, inner.view.element)) nested.push({ outer, inner });
    }
    const r = checkInstances(this.instances, nested);
    this.checked = true;
    this.trace.write({ t, kind: "检查", component: "", summary: `组件都加好后的检查 ${r.ok ? "✓" : "✗"}：${this.instances.length} 个组件，${nested.length} 对嵌套`, lines: r.lines });
    return r;
  }

  /** 对照读到不同：写一条错误，再告诉执行指定的人。 */
  private mismatch(component: string, m: Mismatch): void {
    this.trace.write({ t: this.clock ?? performance.now(), kind: "错误", component, summary: `对照：${component} 的查询「${m.query}」两边不同`, lines: [m.what, `相同的条件：${m.same}`] });
    this.exec.onMismatch?.({ ...m, component });
  }

  // ===== 参数（协议第 5 节）：来源 → 全局 → 最外层的母部件 → … → 母部件 → 本部件，范围小的覆盖大的 =====

  private elementOf(part: Part): unknown {
    return part instanceof Instance ? part.view.element : part.element;
  }

  /** 母部件：元素包含这个部件元素的部件（和路由同一个包含关系），从最外层到最近的。 */
  parentsOf(part: Part): Part[] {
    const el = this.elementOf(part);
    const contains = this.platform.contains;
    const out = [...this.composites, ...this.instances].filter((p) => p !== part && this.elementOf(p) !== el && contains(this.elementOf(p), el));
    out.sort((a, b) => (contains(this.elementOf(a), this.elementOf(b)) ? -1 : 1));
    return out;
  }

  private layerOf(part: Part): ParamLayer {
    return partLayer(part.name, part instanceof Instance ? part.def.params ?? {} : {}, part.overrides);
  }

  /** 一个部件的各层，从远到近：全局、各层母部件、它自己。 */
  private layersOf(part: Part): ParamLayer[] {
    return [globalLayer(this.globalOverrides), ...this.parentsOf(part).map((p) => this.layerOf(p)), this.layerOf(part)];
  }

  /** 这些部件和它们里面的组件实例声明的参数；同名的先取这些部件自己的，再取先加的。组合自己不读参数。 */
  private specsOf(parts: readonly Part[]): ParamSpec[] {
    const inside = this.instances.filter((inst) => parts.some((p) => p === inst || this.parentsOf(inst).includes(p)));
    const ordered = [...inside.filter((inst) => parts.includes(inst)), ...inside.filter((inst) => !parts.includes(inst))];
    const seen = new Set<string>();
    const specs: ParamSpec[] = [];
    for (const inst of ordered) for (const p of declaredSpecs(inst.def)) if (!seen.has(p.id) && seen.add(p.id)) specs.push(p);
    return specs;
  }

  private resolveAll(): void {
    for (const inst of this.instances) inst.params = resolveParams(this.system, this.layersOf(inst), declaredSpecs(inst.def));
  }

  /** 只算到全局层的表：含全部实例声明的参数。 */
  globalParams(): ParamTable {
    return resolveParams(this.system, [globalLayer(this.globalOverrides)], this.specsOf([...this.instances, ...this.composites]));
  }

  /** 部件按嵌套排成树（先序，母部件在前），depth 是母部件的层数。配置区的选择器用。 */
  parts(): { part: Part; depth: number }[] {
    const all: Part[] = [...this.composites, ...this.instances];
    const parentOf = new Map(all.map((p) => [p, this.parentsOf(p).at(-1) ?? null] as const));
    const out: { part: Part; depth: number }[] = [];
    const stack: { part: Part; depth: number }[] = all.filter((p) => parentOf.get(p) === null).reverse().map((part) => ({ part, depth: 0 }));
    while (stack.length) {
      const top = stack.pop()!;
      out.push(top);
      const kids = all.filter((p) => parentOf.get(p) === top.part);
      for (let i = kids.length - 1; i >= 0; i--) stack.push({ part: kids[i], depth: top.depth + 1 });
    }
    return out;
  }

  /**
   * 配置区看一个部件（null 看全局）：各层从远到近，和这个视角下的完整参数表。表列它自己和它里面的部件读的参数
   * （这一层作用于它们）；实例自己读的那几项和它的 params 一样。
   */
  config(part: Part | null): ConfigView {
    const global: ConfigLayer = { ...globalLayer(this.globalOverrides), kind: "全局", distance: null, part: null };
    if (!part) return { part: null, layers: [global], params: this.globalParams() };
    const parents = this.parentsOf(part);
    const layers: ConfigLayer[] = [
      global,
      ...parents.map((p, i): ConfigLayer => ({ ...this.layerOf(p), kind: "母部件", distance: parents.length - i, part: p })),
      { ...this.layerOf(part), kind: "本部件", distance: 0, part },
    ];
    return { part, layers, params: resolveParams(this.system, layers, this.specsOf([part])) };
  }

  /**
   * 改一层的一项（part 为 null 是全局；value 为 null 取消覆盖）。这一层管的实例（它自己和它里面的，全局是全部）重算，
   * 日志列出每个实例的最终值变了没有、取自哪里。
   */
  setOverride(part: Part | null, id: ParamId, value: number | null, t: number): void {
    const layer = part ? part.overrides : this.globalOverrides;
    if (value === null) delete layer[id];
    else layer[id] = value;
    const affected = this.governedBy(part);
    const label = this.globalParams()[id]?.label ?? id;
    const lines: string[] = [];
    for (const inst of affected) {
      const old = inst.params[id];
      inst.params = resolveParams(this.system, this.layersOf(inst), declaredSpecs(inst.def));
      const now = inst.params[id];
      if (!old || !now) continue;
      lines.push(old.value === now.value ? `${inst.name}：仍是 ${cite(now)}` : `${inst.name}：${old.value} → ${cite(now)}`);
    }
    const where = part ? `部件「${part.name}」` : "全局";
    this.trace.write({ t, kind: "参数", component: part instanceof Instance ? part.name : "", summary: `参数：${where}层 ${label} ${value === null ? "取消覆盖" : `覆盖为 ${value}`}`,
      lines: lines.length ? lines : ["这一层管的部件都不读这一项"] });
    if (this.checked && (id === "dragWidth" || id === "dragHeight")) this.checkInstances(t);
  }

  /**
   * 把一层整个换成这一组覆盖（part 为 null 是全局；协议第 5 节“整层替换”）：不在这一组里的项取消覆盖。
   * 这一层管的实例只重算一次，日志写一条，按实例列出值变了的项。手感切换、套件的手感用它。
   */
  setOverrides(part: Part | null, overrides: ParamOverrides, t: number): void {
    const layer = part ? part.overrides : this.globalOverrides;
    const before: ParamOverrides = { ...layer };
    for (const id of Object.keys(layer)) delete layer[id];
    for (const [id, value] of Object.entries(overrides)) if (value !== undefined) layer[id] = value;
    const changed = [...new Set([...Object.keys(before), ...Object.keys(layer)])].filter((id) => before[id] !== layer[id]);
    const labels = this.globalParams();
    const lines: string[] = [Object.keys(layer).length ? `这一层：${Object.entries(layer).map(([id, v]) => `${labels[id]?.label ?? id} ${v}`).join("；")}` : "这一层：不覆盖任何项"];
    const diffs = this.reresolve(this.governedBy(part), changed);
    lines.push(...(diffs.length ? diffs : ["这一层管的部件读到的值都没变"]));
    const where = part ? `部件「${part.name}」` : "全局";
    this.trace.write({ t, kind: "参数", component: part instanceof Instance ? part.name : "", summary: `参数：${where}层整层换成 ${Object.keys(layer).length} 项覆盖（${changed.length} 项和之前不同）`, lines });
    if (this.checked && (changed.includes("dragWidth") || changed.includes("dragHeight"))) this.checkInstances(t);
  }

  /**
   * 换系统读数（协议第 4.4 节：宿主启动后读到、系统设置变化时再给一次；null 是读取失败）。全部实例重算一次，日志写一条“参数”。
   */
  setSystem(system: SystemReadings | null, t: number): void {
    this.system = system;
    const read = system ? Object.entries(system).map(([k, v]) => `${k} ${v ?? "读取失败"}`).join("；") : "全部读取失败，系统项用默认值";
    const diffs = this.reresolve(this.instances, null);
    this.trace.write({ t, kind: "参数", component: "", summary: `参数：系统读数${system ? "更新" : "读取失败"}`, lines: [`系统：${read}`, ...(diffs.length ? diffs : ["各部件读到的值都没变"])] });
    if (this.checked) this.checkInstances(t);
  }

  /** 重算这些实例的参数，按实例列出值变了的项（ids 为 null 时看全部项）。 */
  private reresolve(insts: readonly Instance[], ids: readonly ParamId[] | null): string[] {
    const lines: string[] = [];
    for (const inst of insts) {
      const old = inst.params;
      inst.params = resolveParams(this.system, this.layersOf(inst), declaredSpecs(inst.def));
      const diffs = (ids ?? Object.keys(inst.params)).filter((id) => old[id] && inst.params[id] && old[id].value !== inst.params[id].value)
        .map((id) => `${inst.params[id].label} ${old[id].value} → ${cite(inst.params[id])}`);
      if (diffs.length) lines.push(`${inst.name}：${diffs.join("；")}`);
    }
    return lines;
  }

  /** 一层管的实例：部件自己和它里面的；全局是全部。 */
  private governedBy(part: Part | null): Instance[] {
    return part ? this.instances.filter((inst) => inst === part || this.parentsOf(inst).includes(part)) : this.instances;
  }

  setAnimationMode(mode: AnimationMode, t: number): void {
    this.animation = mode;
    this.trace.write({ t, kind: "参数", component: "", summary: `动画档位：${ANIMATION_LABELS[mode]}`, lines: [
      mode === "normal" ? "手放开后按力场推进和到终态逐帧走" : "按力场推进和到终态直接到终点；手带着时位置仍是手的位置",
      "档位只影响动画输出；业务逻辑输出和框架输出不变，落点照样按力场算",
    ] });
  }

  feed(raw: RawInput): void {
    // 先推出这个时刻以前该来的按住重复，次序和时刻都不乱
    this.pumpHoldRepeat(raw.t);
    if (raw.type === "pointer") this.lastPointerId = raw.pointerId;
    this.clock = raw.t;
    try {
      this.input.feed(raw, (change) => this.route(change));
    } finally {
      this.clock = null;
    }
    this.wake();
  }

  // ===== 路由（协议第 4.2 节） =====

  /** 包含事件目标的组件和组合，从最内层到最外层。包含同一个目标的元素一层套一层，内层被外层包含。 */
  private chainAt(target: unknown): (Instance | Composite)[] {
    const nodes: { node: Instance | Composite; el: unknown }[] = [];
    const contains = this.platform.contains;
    for (const inst of this.instances) if (contains(inst.view.element, target)) nodes.push({ node: inst, el: inst.view.element });
    for (const comp of this.composites) if (contains(comp.element, target)) nodes.push({ node: comp, el: comp.element });
    nodes.sort((a, b) => (a.el === b.el ? 0 : contains(b.el, a.el) ? -1 : 1));
    return nodes.map((n) => n.node);
  }

  private route(change: InputChange): void {
    const c = change.change;
    const raw = change.raw;
    if (c.kind === "event" && (c.id === "loss.blur" || c.id === "loss.focus" || c.id === "loss.hidden" || c.id === "loss.visible")) {
      let delivered = false;
      for (const inst of this.instances) {
        if (!inst.reads(change)) continue;
        if (this.evaluate(inst, change, `广播：${LOSS_LABELS[c.id]}是全局事件，发给所有读它的组件`) === true) delivered = true;
      }
      if (!delivered) this.unread(change, "", "没有组件读", "全局事件，当前没有组件读它");
      return;
    }
    const pointerish = c.id.startsWith("pointer.") || c.id === "request.contextmenu" || c.id === "loss.pointercancel" || c.id === "loss.lostcapture" || c.id === "loss.leftzone";
    if (pointerish && this.holder) {
      this.evaluate(this.holder.inst, change, `独占指针：${this.holder.inst.name} 自 #${this.holder.since} 起独占`);
      return;
    }
    if (c.id === "loss.leftzone") this.leave(change, []);
    const chain = this.chainAt(raw.target);
    if (c.key === "pointer.position~") this.leave(change, chain);
    // 落在最内层组件声明的原生控件上：交给浏览器，不进路由（内外层都不收）
    const innermost = chain.find((n): n is Instance => n instanceof Instance);
    if (innermost?.view.nativeControl?.(raw.target)) {
      if (c.key !== "pointer.position~") this.unread(change, innermost.name, "原生控件", `事件目标是 ${innermost.name} 里交给浏览器的原生控件（呈现层声明，协议第 4.2 节第 2 项）`);
      return;
    }
    if (chain.length === 0) {
      // 不在任何组件上的指针和键盘变化不记，否则点页面控件也会刷屏；请求和输入丢失事件照记。
      if (c.kind === "request" || c.kind === "event") this.unread(change, "", "没有组件", "事件目标不在任何组件内");
      return;
    }
    // 从最内层往外问：读就由它求值，不读就问外一层；禁用的组件跳过。
    const how = c.id.startsWith("key.") ? "焦点在" : c.id === "request.activate" ? "激活目标在" : "命中：事件目标在";
    const start = `${how} ${chain[0].name} 内`;
    const asked: string[] = [];
    const why = (to: string) => (asked.length ? `${start}；${asked.join("；")}；交给 ${to}` : start);
    for (const node of chain) {
      if (node instanceof Instance) {
        if (!node.host.enabled()) { asked.push(`${node.name} 已禁用，不参与路由`); continue; }
        if (node.reads(change)) {
          const taken = this.evaluate(node, change, why(node.name));
          if (taken === true) return;
          asked.push(taken);
          continue;
        }
        asked.push(`${node.name}（${node.mode}）不读`);
        continue;
      }
      // 组合：目标在它的某个子组件上时，那个子组件已经问过了，不在组合里另找。
      if (!pointerish || node.children.some((child) => this.platform.contains(child.view.element, raw.target))) continue;
      const r = node.route(this.input.position());
      if (!r) { asked.push(`${node.name} 不把这里交给子组件`); continue; }
      const via = `区域路由：落在${node.name}的空白处，${r.why}`;
      // 组合和子组件谁先移除由宿主定：交给已移除的子组件当作不交
      if (!this.instances.includes(r.child)) { asked.push(`${via}，但它已移除`); continue; }
      if (!r.child.host.enabled()) { asked.push(`${via}，但它已禁用`); continue; }
      if (r.child.reads(change)) {
        asked.push(via);
        const taken = this.evaluate(r.child, change, why(r.child.name));
        if (taken === true) return;
        asked.push(taken);
        continue;
      }
      asked.push(`${via}，${r.child.name}（${r.child.mode}）不读`);
    }
    const inner = chain.find((n): n is Instance => n instanceof Instance);
    if (c.key !== "pointer.position~") this.unread(change, inner?.name ?? "", asked.join("，"), `${start}；${asked.join("；")}`);
  }

  /**
   * 指针离开（协议第 4.2 节）：这次位置变化的事件目标不在上一次所在的某个组件里了，或者指针出了响应区（chain 为空）。
   * 那个组件读“指针离开”（且没禁用）时，先由它对“指针离开”求值，再照常路由这次变化。有组件独占指针时不走到这里，独占时不算离开。
   */
  private leave(change: InputChange, chain: readonly (Instance | Composite)[]): void {
    const now = new Set(chain.filter((n): n is Instance => n instanceof Instance));
    const left = [...this.hovered].filter((inst) => !now.has(inst));
    this.hovered = now;
    for (const inst of left) {
      const lc: InputChange = { ...change, change: { kind: "event", id: POINTER_LEAVE, key: POINTER_LEAVE } };
      if (inst.host.enabled() && inst.reads(lc)) this.evaluate(inst, lc, chain.length === 0 && change.change.id === "loss.leftzone" ? `指针离开：指针出了响应区，不在 ${inst.name} 内了` : `指针离开：事件目标不在 ${inst.name} 内了`);
    }
  }

  /** 没有组件读：交给浏览器，默认动作照常。 */
  private unread(change: InputChange, component: string, who: string, why: string): void {
    const label = changeLabel(change.change);
    this.trace.write({
      t: change.t, kind: "未读取", component,
      summary: `${label} → ${who}，交给浏览器`,
      lines: [this.sourceLine(change), why],
      group: `unread:${component}:${who}:${change.change.key}`,
    });
  }

  // ===== 求值 =====

  /** 事实和取值能读的东西。 */
  private context(inst: Instance, t: number, hand = this.handState(inst, t)): FactContext {
    const ch = inst.ch;
    return {
      t, input: this.input, params: inst.params, hostValue: inst.host.value(),
      channel: { x: ch.x, v: ch.v, hand, origin: ch.origin, segment: inst.segment },
      geometry: inst.geometry(), def: inst.def, rect: this.platform.rect(inst.view.element), bindingLabel: inst.view.bindingLabel,
      pressedOn: this.pressedOn(inst),
      handUnbounded: ch.hand === null ? null : inst.def.channel.handMap ? inst.def.channel.handMap.to(ch.hand, inst.params) : ch.hand,
    };
  }

  /** 手：手带着时，手带着物体去的位置（最后一次采样，经手的映射）和速度；手不在时为 null。 */
  private handState(inst: Instance, t: number): { x: Value; v: Value } | null {
    const ch = inst.ch;
    if (ch.hand === null || !ch.samples.length) return null;
    return { x: ch.samples[ch.samples.length - 1].x, v: this.kinematics(inst, t).v };
  }

  /** 最近一次左键按下时，事件目标在不在这个组件的元素里。 */
  private pressedOn(inst: Instance): boolean {
    const p = this.input.lastPress("pointer.left");
    return p !== null && p.target !== null && this.platform.contains(inst.view.element, p.target);
  }

  /**
   * 求值。收下了（包括不读、出错，都已记日志）返回 true；命中的行写了“交给外层”时不改任何状态，
   * 返回一句理由，路由接着问外一层（协议第 4.2 节）。
   */
  private evaluate(inst: Instance, change: InputChange, routeWhy: string): true | string {
    const c = change.change;
    const t = change.t;
    if (!inst.reads(change)) {
      if (c.key !== "pointer.position~") this.unread(change, inst.name, `${inst.name}（${inst.mode}）不读`, `${routeWhy}；${inst.mode} 模式的读取里没有它`);
      return true;
    }
    const before = { mode: inst.mode, segment: inst.segment };
    const lines: string[] = [this.sourceLine(change), ...this.inputLines(change, inst.params), `路由：${routeWhy}`, `当前：模式 ${before.mode}${before.segment !== null ? `，所在段 ${fmt(before.segment)}` : ""}`];

    // 0. 按力场推进、到终态中：先推进到这一刻，事实和取值读的都是这一刻的位置和速度
    this.advance(inst, t);

    // 1. 手带着时先按当前指针位置更新位置（手的位置、速度、所在段），不经转移表。
    //    每次求值都更新，所以停住再松开，用的是松开这一刻的速度。
    let handDone = false;
    if (inst.holds()) {
      lines.push(...this.dragUpdate(inst, t));
      handDone = true;
    }

    // 2. 事实和唯一一行
    const rows = inst.def.rows.filter((r) => (r.from.includes(inst.mode) || r.from.includes("*")) && r.on.includes(c.key));
    const factIds = [...new Set(rows.flatMap((r) => Object.keys(r.when ?? {}) as FactId[]))];
    const factCtx = this.context(inst, t);
    const facts = new Map(factIds.map((id) => [id, FACTS[id](factCtx)]));
    for (const f of facts.values()) lines.push(`事实：${f.explain}`);
    const matched = rows.filter((r) => Object.entries(r.when ?? {}).every(([k, v]) => facts.get(k as FactId)?.value === v));
    if (matched.length !== 1) {
      this.trace.write({
        t, kind: "错误", component: inst.name,
        summary: `${changeLabel(c)} → ${inst.name}：${matched.length === 0 ? "没有命中的行（缺失）" : `命中 ${matched.length} 行（冲突）`}`,
        lines: [...lines, ...rows.map((r) => `候选：${rowText(inst.def, r)}`)],
      });
      return true;
    }
    const row = matched[0];
    if (row.out?.some((o) => o.tag === "框架" && o.act === "交给外层")) {
      const why = [...facts.values()].map((f) => f.explain.split("：")[0]).join("，");
      return `${inst.name}（${inst.mode}）命中 #${inst.def.rows.indexOf(row) + 1}${why ? `（${why}）` : ""}，交给外层`;
    }
    lines.push(`命中行：${rowText(inst.def, row)}${row.note ? `（${row.note}）` : ""}`);

    // 3. 进入独占模式：结束正在进行的运动（速度 0），记起点和按下时手所在处；进入手模式就先按手更新位置。
    const next = row.to === "=" ? inst.mode : row.to;
    if (!inst.exclusive() && inst.exclusive(next)) {
      this.advance(inst, t);
      const was = this.moving(inst);
      inst.ch.motion = null;
      if (was) {
        inst.ch.v = inst.zero;
        lines.push(`${tag(inst)}：${was}中被按住，在 ${fmt(inst.ch.x)} 结束，速度 0`);
      }
      inst.ch.origin = inst.ch.x;
      inst.ch.samples = [{ t, x: inst.ch.x }];
      // 手势开始：读这一次的几何，一直到离开独占模式（几何查询的优化算法用它）
      inst.ch.gesture = null;
      const geom = inst.geometry();
      inst.ch.gesture = geom ? { geometry: geom, frame: frameOf(inst.def.channel, geom) } : null;
      const frame = inst.geometryFrame();
      inst.ch.handStart = jumps(row) && frame ? pointerValue(frame, this.input) : handFrom(inst.def.channel, inst.ch.x, inst.params);
      lines.push(geom
        ? `起点：进入独占模式，记下逻辑中心（${geom.center}）的位置 ${fmt(inst.ch.x)} 作为起点`
        : `起点：进入独占模式，${tag(inst)} 记下当前位置 ${fmt(inst.ch.x)} 作为起点（没有逻辑中心，不跟手）`);
      if (jumps(row) && frame) lines.push(`按下时手所在处：这一行写了“跳到指针处”，取指针处 ${fmt(inst.ch.handStart)}，逻辑中心跟着手到那里`);
      else if (inst.def.channel.handMap) lines.push(`按下时手所在处：位置 ${fmt(inst.ch.x)} 按手的映射「${inst.def.channel.handMap.name}」反推，是 ${fmt(inst.ch.handStart)}`);
    }
    const letGo = inst.holds() && !inst.holds(next);
    if (inst.holds(next) && !handDone) lines.push(...this.dragUpdate(inst, t));

    // 4. 手放开：速度按松手吸能处理。停住点是落点或者没写停住点时，没有被带向的放置点，从当时的位置和速度出发按力场推进到停下，得到落点；
    //    停住点是别的值时，它是终态，力场带物体去那里。
    const stop = stopOf(row);
    const byField = letGo && (stop === null || ("value" in stop.value && stop.value.value === LANDING));
    let motion: FieldMotion | null = null;
    const motionFrame = byField ? inst.geometryFrame() : null;
    // 手放开的那一行，取值和力场读放开那一刻的手
    const letGoHand = letGo ? this.handState(inst, t) : null;
    if (letGo) lines.push(this.letGo(inst, t));
    if (byField) {
      motion = inst.q.motion(inst.def.channel, motionFrame, inst.params, inst.def.channel.field, inst.ch.x, inst.ch.v, null, { letGo: letGoHand, moving: this.movingFor(inst, t) });
      lines.push(...motion.lines.map((s) => `${tag(inst)}：${s}`));
    }

    // 5. 取停住点和调用参数：拖动开始那一刻用更新后的位置，松手的落点用按力场停下的位置。
    //    停住点映射成通道的停住点的调用：调用里的“停住点”换成它的值。
    const valueCtx: ValueContext<Value> = letGo ? this.context(inst, t, letGoHand) : this.context(inst, t);
    const stepped = inst.def.channel.step ? " 按步长取整" : "";
    const valueOf = (name: string): ValueResult => {
      if (name === CURRENT) return { value: stepValue(inst.def.channel, inst.ch.x), explain: `通道的位置 ${fmt(inst.ch.x)}${stepped}` };
      if (name === ORIGIN) return { value: stepValue(inst.def.channel, inst.ch.origin), explain: `进入独占模式时记下的起点 ${fmt(inst.ch.origin)}${stepped}` };
      if (name !== LANDING) return inst.def.values[name](valueCtx);
      if (!motion) return { value: NaN, explain: "错误：这一行没有按力场推进，没有落点（落点只能是手放开的行的停住点）" };
      return { value: motion.landing, explain: `手放开后按力场停下的位置 ${fmt(motion.rest)}${stepped}` };
    };
    const literal = (a: { literal: number | boolean | string }) => ({ value: a.literal, explain: `字面量 ${String(a.literal)}` });
    const stopValue = stop ? ("value" in stop.value ? valueOf(stop.value.value) : literal(stop.value) as ValueResult) : null;
    const argOf = (a: Arg): { value: unknown; explain: string } => {
      if (!("value" in a)) return literal(a);
      if (a.value === STOP_POINT && stop && stopValue) return { value: stopValue.value, explain: `${argText(stop.value)} = ${stopValue.explain}` };
      return valueOf(a.value);
    };
    const callOf = (o: Call) => {
      const vals = o.args.map(argOf);
      return { port: o.port, args: vals.map((v) => v.value), explains: o.args.map((a, i) => `${argText(a)} = ${vals[i].explain}`) };
    };
    const stopCall = inst.def.channel.stopCall;
    if (stop && !stopCall) lines.push(`错误：行上写了停住点，通道没有写停住点的调用，不调用（机器检查报出）`);
    const calls = (row.out ?? []).flatMap((o) => (o.tag === "业务" ? [callOf(o)] : o.tag === "停住点" && stopCall ? [callOf(stopCall)] : []));

    // 6. 换模式。手放开时所在段归无。物体按停住点去：落点或者没写，按力场推进；当前值，就地停住；其余是终态，由力场带过去。
    inst.mode = next;
    if (letGo) {
      if (inst.segment !== null) lines.push(`所在段：手放开，所在段 ${fmt(inst.segment)} → 无`);
      inst.segment = null;
    }
    if (motion) lines.push(...this.startFree(inst, motion, motionFrame, t));
    else if (stop && stopValue) lines.push(...this.goStop(inst, stop.value, stopValue, t, letGoHand));
    if (before.mode !== inst.mode) lines.push(`转移：模式 ${before.mode} → ${inst.mode}`);

    // 7. 输出：框架（行上写的认领、焦点；进出独占模式时捕获、释放指针），
    //    动画（模式和所在段变了就记，它们是驱动量；行上写的跳到指针处），最后业务调用
    const outLines: string[] = [];
    // “交给外层”的行在前面已经返回，走到这里的框架输出只有认领和焦点
    for (const o of row.out ?? []) if (o.tag === "框架" && o.act !== "交给外层") outLines.push(`框架：${o.act}（${this.framework(inst, o.act, change)}）`);
    const wasExclusive = inst.exclusive(before.mode);
    if (!wasExclusive && inst.exclusive()) {
      this.platform.capture(inst.view.element, this.lastPointerId);
      outLines.push(`框架：捕获指针（进入独占模式 ${inst.mode}；setPointerCapture(${this.lastPointerId})，指针移出组件和窗口也不断）`);
    } else if (wasExclusive && !inst.exclusive()) {
      inst.ch.gesture = null;
      this.platform.release(inst.view.element, this.lastPointerId);
      outLines.push(`框架：释放指针（离开独占模式；releasePointerCapture(${this.lastPointerId})）`);
    }
    const seg = (s: Value | null) => (s === null ? "无" : fmt(s));
    if (before.mode !== inst.mode) outLines.push(`动画：模式 ${before.mode} → ${inst.mode}（驱动量）`);
    if (before.segment !== inst.segment) outLines.push(`动画：所在段 ${seg(before.segment)} → ${seg(inst.segment)}（驱动量）`);
    for (const o of row.out ?? []) if (o.tag === "动画") outLines.push(`动画：跳到指针处（按下时手在 ${fmt(inst.ch.handStart)}）`);
    for (const call of calls) outLines.push(`业务：${call.port}(${call.args.map(argValue).join(", ")})${call.explains.length ? `；${call.explains.join("；")}` : ""}`);
    if (outLines.length === 0) outLines.push("输出：无");
    lines.push(...outLines);

    this.toDraw.add(inst);
    const callSummary = calls.length ? `；调用 ${calls.map((k) => `${k.port}(${k.args.map(argValue).join(", ")})`).join("、")}` : "";
    const entry = this.trace.write({
      t, kind: "输入", component: inst.name,
      summary: `${changeLabel(c)} → ${inst.name}：${before.mode}${before.mode !== inst.mode ? ` → ${inst.mode}` : ""}${callSummary}`,
      lines,
      row: inst.def.rows.indexOf(row) + 1,
      group: c.key === "pointer.position~" && before.mode === inst.mode ? `in:${inst.name}:${c.key}:${inst.def.rows.indexOf(row)}` : undefined,
    });
    // 路由归属随模式走：进入独占模式就独占指针，离开就交还
    if (inst.exclusive() && this.holder?.inst !== inst) this.holder = { inst, since: entry.seq, pointerId: this.lastPointerId };
    if (!inst.exclusive() && this.holder?.inst === inst) this.holder = null;
    this.syncHoldRepeat(inst, t, before.mode);
    for (const call of calls) inst.host.call(call.port, call.args, `${inst.name} 的调用（日志 #${entry.seq}）`);
    // 8. 调用之后：手不在时，宿主值和物体将要停的值不同（取消、宿主没收、点击后宿主值变了），就到终态。
    //    宿主同步改值时已在 hostChanged 里对过，这里只在还不同时才动。
    const carry = this.reconcile(inst, t, `日志 #${entry.seq} 之后`);
    if (carry.length) this.trace.write({ t, kind: "到位", component: inst.name, summary: `${inst.name} ${tag(inst)} 到终态 ${fmt(inst.host.value())}`, lines: carry });
    return true;
  }

  private framework(inst: Instance, act: "认领" | "焦点", change: InputChange): string {
    if (act === "认领") {
      const ev = change.raw.event as { preventDefault?: () => void } | undefined;
      ev?.preventDefault?.();
      return "阻止浏览器默认动作";
    }
    this.platform.focus(inst.view.element);
    return "focus()，之后的键盘量交给它";
  }

  // ===== 通道：手和力场（协议第 4.5 节） =====

  /**
   * 手带着：手的位置（经手的映射、不出边界）。手绝对吸附时位置就是它，速度是位置在速度窗口内的变化率；
   * 手只是放置点时，物体从这一刻的位置和速度出发由力场带向它。所在段是此刻静止松手会停到的输出映射点。
   */
  private dragUpdate(inst: Instance, t: number): string[] {
    const frame = inst.geometryFrame();
    if (!frame) return [`${tag(inst)}：没有几何，手带不动`];
    const ch = inst.ch;
    const hand = handPosition(inst.def.channel, ch.handStart, this.input, frame);
    const held = holdAt(inst.def.channel, hand.hand, inst.params);
    const placement = inst.def.channel.handIsPlacement === true;
    let towardLines: string[] = [];
    if (placement) {
      // 手只是放置点：先推进到这一刻，再从当时的位置和速度出发由力场带向手；采样记的是手的位置（经手的映射），算手的速度
      this.advance(inst, t);
      towardLines = this.startMotion(inst, { toward: "手", value: held.x }, t, frame, null);
    } else {
      ch.x = held.x;
      ch.motion = null;
    }
    ch.hand = hand.hand;
    ch.lastT = t;
    pushSample(ch.samples, { t, x: held.x });
    const k = this.kinematics(inst, t);
    if (!placement) ch.v = k.v;
    const out = [hand.explain, held.line, ...towardLines];
    if (inst.def.channel.mappingPoints?.length) {
      inst.segment = restPoint(inst.def.channel, frame, inst.params, inst.def.channel.field, ch.x);
      out.push(inst.segment !== null ? `所在段：此刻静止松手会停到输出映射点 ${fmt(inst.segment)}` : "所在段：此刻静止松手不会停到输出映射点上");
    }
    out.push(placement ? `手的${k.explain}` : k.explain);
    if (!placement) out.push(forceLine(this.handForces(inst, frame, ch.x, k.v, k.a), unitOf(inst)));
    return out.map((s) => `${tag(inst)}：${s}`);
  }

  /** 手带着时的各项加速度：力场照常算，被手盖过；手 = 加速度 − 力场（手绝对吸附）。 */
  private handForces(inst: Instance, frame: Frame, x: Value, v: Value, a: Value): Force<Value>[] {
    const field = fieldForce(inst.def.channel, frame, inst.params, inst.def.channel.field, { x, v });
    return [{ name: "手", a: inst.value(sub(vec(a), vec(field.a))) }, field];
  }

  /**
   * 手带着时位置的速度和加速度，都从位置的采样算，和指针速度同一算法：
   * 速度 = 位置在速度窗口内的变化率；加速度 = 这一刻的速度减去窗口起点那一刻的速度，除以窗口长度。
   * 窗口不早于最近一次指针按下。指针停住时没有新的输入，按当前时刻再算，速度和加速度会降到 0。
   */
  private kinematics(inst: Instance, t: number): { v: Value; a: Value; explain: string } {
    const W = inst.params.velocityWindow;
    let press = -Infinity;
    for (const b of POINTER_BUTTONS) {
      const p = this.input.lastPress(b.id);
      if (p && p.t > press) press = p.t;
    }
    // 每个分量各算各的（平面上两个分量用同一个窗口）
    const axis = (c: "x" | "y") => {
      const read = (s: { x: Value }) => vec(s.x)[c];
      const velocityAt = (at: number) => windowRate(inst.ch.samples, read, Math.max(at - W.value, press), at, valueAt(inst.ch.samples, at, read));
      const rv = velocityAt(t);
      const v0 = velocityAt(t - rv.span).rate;
      return { rv, dv: rv.rate - v0, a: rv.span > 0 ? ((rv.rate - v0) / rv.span) * 1000 : 0 };
    };
    const ax = axis("x");
    const ay = inst.plane ? axis("y") : { rv: { rate: 0, span: ax.rv.span, delta: 0 }, dv: 0, a: 0 };
    const span = ax.rv.span;
    const v = inst.value({ x: ax.rv.rate, y: ay.rv.rate });
    const a = inst.value({ x: ax.a, y: ay.a });
    const clipped = span < W.value ? `，窗口从本次按下开始，只有 ${span.toFixed(1)} ms` : "";
    const u = unitOf(inst);
    return {
      v,
      a,
      explain: `速度 ${fmt(v)} ${u}/s = 位移 ${fmt(inst.value({ x: ax.rv.delta, y: ay.rv.delta }))} ÷ ${span.toFixed(1)} ms；`
        + `加速度 ${fmt(a)} ${u}/s² = 速度变化 ${fmt(inst.value({ x: ax.dv, y: ay.dv }))} ÷ ${span.toFixed(1)} ms（${cite(W)}${clipped}）`,
    };
  }

  /** 手放开：吸掉“松手吸能”比例的动能，速度乘 √(1 − 它)。手只是放置点时接物体这一刻的位置和速度（力场带出来的），不是手的。 */
  private letGo(inst: Instance, t: number): string {
    this.advance(inst, t);
    const r = inst.params.releaseAbsorb;
    const v = inst.ch.v;
    inst.ch.v = inst.value(scale(vec(v), keep(r.value)));
    inst.ch.hand = null;
    return `${tag(inst)}：手放开，速度 ${fmt(v)} → ${fmt(inst.ch.v)} ${unitOf(inst)}/s（${cite(r)}，速度乘 √(1 − ${r.value})）`;
  }

  /** 力场正在带着物体时写它在做什么（带向手、带向终态、按力场推进），没在动时为 null。 */
  private moving(inst: Instance): string | null {
    const m = inst.ch.motion;
    if (!m) return null;
    return m.m.place === null ? "按力场推进" : `带向${m.m.place.toward === "手" ? "手" : "终态"}`;
  }

  /** 物体这次被力场连续带着动了多久（s，到时刻 t）：没在动时为 0。 */
  private movingFor(inst: Instance, t: number): number {
    const m = inst.ch.motion;
    return m ? (t - m.since) / 1000 : 0;
  }

  /**
   * 放置点变了（手动了、终态出现或变了）：从当前位置和速度出发按力场带向它，起一段运动。物体还在动时接着这次的动（since 不变）。
   * 出发处就停着时不动；带向手时停在算出的地方，还由手带着。
   */
  private startMotion(inst: Instance, place: Place, t: number, frame: Frame | null, letGo: { x: Value; v: Value } | null): string[] {
    const ch = inst.ch;
    const since = ch.motion ? ch.motion.since : t;
    const m = inst.q.motion(inst.def.channel, frame, inst.params, inst.def.channel.field, ch.x, ch.v, place, { letGo, moving: (t - since) / 1000 });
    ch.lastT = t;
    if (m.stopped(0)) {
      ch.motion = null;
      ch.x = m.rest;
      ch.v = inst.zero;
    } else ch.motion = { m, t0: t, frames: 0, frame, since };
    return m.lines;
  }

  /**
   * 去停住点（不是落点的）：当前值就地停住，结束正在进行的运动，速度 0；其余是终态，由力场从当前的位置和速度带过去。
   * 调用之后宿主值没跟着变（宿主没收）时，再按宿主值到终态（第 8 步）。
   */
  private goStop(inst: Instance, arg: Arg, stop: ValueResult, t: number, letGo: { x: Value; v: Value } | null): string[] {
    const ch = inst.ch;
    if ("value" in arg && arg.value === CURRENT) {
      const was = this.moving(inst) ?? "没在动";
      ch.motion = null;
      ch.v = inst.zero;
      ch.lastT = t;
      return [`${tag(inst)}：停住点是当前值：就地停住（${was}），停在 ${fmt(ch.x)}，速度 0`];
    }
    return this.startCarry(inst, stop.value, t, `停住点 ${argText(arg)} = ${fmt(stop.value)}`, letGo);
  }

  /** 开始按力场推进（没有被带向的放置点）；出发处就停着、没有几何，或者动画档不是正常时，直接停在终点。物体还在动时接着这次的动。 */
  private startFree(inst: Instance, m: FieldMotion, frame: Frame | null, t: number): string[] {
    const ch = inst.ch;
    const since = ch.motion ? ch.motion.since : t;
    ch.motion = null;
    ch.lastT = t;
    const still = m.stopped(0);
    if (still || !frame || this.animation !== "normal") {
      ch.x = m.rest;
      ch.v = inst.zero;
      return [`${tag(inst)}：力场：${still || !frame ? "出发处就停着" : `动画档 ${ANIMATION_LABELS[this.animation]}，直接停在 ${fmt(m.rest)}`}`];
    }
    ch.motion = { m, t0: t, frames: 0, frame, since };
    return [`${tag(inst)}：开始按力场推进，${msText(m.durationMs)} ms 后停在 ${fmt(m.rest)}`];
  }

  /** 力场带着的这一段走完了：停在它停的地方。按力场推进的，再和宿主值对；带向终态没到是场写错了，记一条错误，不再重新到终态（协议第 4.5 节）。 */
  private finishMotion(inst: Instance, t: number): void {
    const ch = inst.ch;
    const mo = ch.motion!;
    ch.motion = null;
    ch.x = mo.m.rest;
    ch.v = inst.zero;
    ch.lastT = t;
    const place = mo.m.place;
    // 带向手：跟上了手，停在力场停的地方，还由手带着
    if (place?.toward === "手") return;
    const took = `用时 ${(t - mo.t0).toFixed(0)} ms、${mo.frames} 帧（推进本身 ${msText(mo.m.durationMs)} ms）`;
    // 按需推的这一段停下时才写特例点、经过和停下（一次推完的已在开始时写了，这里为空）
    const end = mo.m.endLines().map((s) => `${tag(inst)}：${s}`);
    if (place === null) {
      const lines = [`停下：${fmt(mo.m.rest)}，${took}`, ...end, ...this.reconcile(inst, t, "停下后")];
      this.trace.write({ t, kind: "到位", component: inst.name, summary: `${inst.name} ${tag(inst)} 停下 ${fmt(mo.m.rest)}`, lines });
      return;
    }
    this.trace.write(mo.m.reached
      ? { t, kind: "到位", component: inst.name, summary: `${inst.name} ${tag(inst)} 到终态 ${fmt(place.value)}，${took}`, lines: [`力场「${inst.def.channel.field.name}」停在终态`, ...end] }
      : { t, kind: "错误", component: inst.name, summary: `${inst.name} ${tag(inst)} 力场停在 ${fmt(mo.m.rest)}，没到终态 ${fmt(place.value)}`, lines: [took, "场写错了：物体停在这里，不再重新到终态", ...end] });
  }

  /** 物体将要停的值：带向终态中是终态；按力场推进中是停下的位置按步长取整；否则是当前位置按步长取整（值的定义，画面不取整）。 */
  private willRest(inst: Instance): { value: Value; text: string } {
    const mo = inst.ch.motion;
    if (mo?.m.place?.toward === "终态") return { value: mo.m.place.value, text: `终态 ${fmt(mo.m.place.value)}` };
    if (mo && mo.m.place === null) {
      const value = stepValue(inst.def.channel, mo.m.rest);
      return { value, text: `按力场停下的位置 ${fmt(mo.m.rest)}（值 ${fmt(value)}）` };
    }
    const x = inst.ch.x;
    const value = stepValue(inst.def.channel, x);
    return { value, text: sameValue(value, x) ? `当前位置 ${fmt(x)}` : `当前位置 ${fmt(x)}（值 ${fmt(value)}）` };
  }

  /** 手不在、而且宿主值和物体将要停的值不同时，开始到终态。返回日志行；不用动时返回空。 */
  private reconcile(inst: Instance, t: number, why: string): string[] {
    if (inst.holds()) return [];
    this.advance(inst, t);
    const target = inst.host.value();
    const will = this.willRest(inst);
    if (sameValue(will.value, target)) return [];
    return this.startCarry(inst, target, t, `${why}：宿主值 ${fmt(target)}，物体将停在${will.text}，不同`, null);
  }

  /**
   * 到终态：终态 to 是被带向的放置点，力场从当前位置和速度出发带过去，整段开始时算好；
   * 动画档不是正常时直接到；出发处就停着时不动。letGo 是这一段从手放开开始时放开那一刻的手。
   */
  private startCarry(inst: Instance, to: Value, t: number, why: string, letGo: { x: Value; v: Value } | null): string[] {
    this.advance(inst, t);
    const ch = inst.ch;
    if (this.animation !== "normal") {
      ch.motion = null;
      ch.lastT = t;
      ch.x = to;
      ch.v = inst.zero;
      return [`${tag(inst)}：${why}；动画档 ${ANIMATION_LABELS[this.animation]}，直接到 ${fmt(to)}`];
    }
    const lines = this.startMotion(inst, { toward: "终态", value: to }, t, inst.geometryFrame(), letGo);
    return [`${tag(inst)}：${why}；到终态`, ...lines.map((x) => `${tag(inst)}：${x}`)];
  }

  private hostChanged(inst: Instance, old: Value, next: Value, cause: string): void {
    const t = this.clock ?? performance.now();
    const lines = [`原因：${cause}`, `框架：无障碍状态 ${inst.view.aria(next)}`];
    if (inst.holds()) lines.push(`${tag(inst)}：手在带，不动；手放开后再和宿主值对`);
    else {
      const carry = this.reconcile(inst, t, "宿主值变化");
      lines.push(...(carry.length ? carry : [`${tag(inst)}：物体将停在${this.willRest(inst).text}，就是宿主值，不用带`]));
    }
    lines.push("不调用：宿主值不进转移表（不回声）");
    this.toDraw.add(inst);
    this.trace.write({ t, kind: "宿主", component: inst.name, summary: `${inst.name} 宿主值 ${fmt(old)} → ${fmt(next)}`, lines, group: `host:${inst.name}:${inst.mode}` });
    this.wake();
  }

  /** 推进到时刻 t：读这一段运动在这一刻的位置和速度。 */
  private advance(inst: Instance, t: number): void {
    const ch = inst.ch;
    const run = ch.motion;
    // 事件晚到（时刻早于上一帧画的时刻）：正在走的运动按事件的时刻读回来，不从帧推进到的状态出发
    if (t <= ch.lastT && !(run && t >= run.t0 && t < ch.lastT)) return;
    if (run) {
      const s = run.m.at(t - run.t0);
      ch.x = s.x;
      ch.v = s.v;
    }
    ch.lastT = t;
  }

  /**
   * 按住重复的起止：组件独占指针、左键按住、这个模式读它时才有。从不读它的模式（或别的组件）进来，从这一刻起算；
   * 在读它的模式之间换，接着原来的；否则结束。
   */
  private syncHoldRepeat(inst: Instance, t: number, beforeMode: Mode): void {
    const reads = (m: Mode) => readsOf(inst.def, m).has(HOLD_REPEAT);
    const on = this.holder?.inst === inst && this.input.isDown("pointer.left") && reads(inst.mode);
    if (!on) {
      if (this.holdRepeat?.inst === inst) this.holdRepeat = null;
      return;
    }
    if (this.holdRepeat?.inst === inst && reads(beforeMode)) return;
    this.holdRepeat = { inst, mode: inst.mode, since: t, next: t + inst.params.holdRepeatDelay.value, n: 0 };
  }

  /** 推出到时刻 t 为止该来的按住重复：每次的时刻就是该来的时刻，一次推出几次都按次序求值。 */
  private pumpHoldRepeat(t: number): void {
    let guard = 0;
    while (this.holdRepeat && this.holdRepeat.next <= t && guard++ < 1000) {
      const r = this.holdRepeat;
      const due = r.next;
      r.n++;
      const p = r.inst.params;
      const source = `由时间推出：${r.inst.name} 进入${r.mode}后 ${(due - r.since).toFixed(0)} ms，第 ${r.n} 次按住重复（${cite(p.holdRepeatDelay)}；${cite(p.holdRepeatInterval)}）`;
      r.next = due + p.holdRepeatInterval.value;
      const saved = this.clock;
      this.clock = due;
      try {
        this.route(this.input.holdRepeat(due, source));
      } finally {
        this.clock = saved;
      }
    }
  }

  /**
   * 要不要下一帧（协议第 4.1 节“帧按需”）：有通道被力场带着、有等着画的实例、有按住重复在等。
   */
  private needsFrame(): boolean {
    return this.toDraw.size > 0 || this.holdRepeat !== null || this.instances.some((inst) => inst.ch.motion !== null);
  }

  /** 要下一帧而还没请过，就请一帧。 */
  private wake(): void {
    if (this.frameRequested || !this.needsFrame()) return;
    this.frameRequested = true;
    this.platform.requestFrame();
  }

  /** 每帧调用一次：推出该来的按住重复，推进力场带着的通道，再把状态变了的实例画一次；还要下一帧就再请一帧。返回还有没有在动的通道。 */
  frame(t: number): boolean {
    this.frameRequested = false;
    this.pumpHoldRepeat(t);
    let moving = false;
    for (const inst of this.instances) {
      const ch = inst.ch;
      if (!ch.motion) continue;
      this.advance(inst, t);
      const mo = ch.motion;
      mo.frames++;
      if (this.animation !== "normal" || mo.m.stopped(t - mo.t0)) this.finishMotion(inst, t);
      if (ch.motion) moving = true;
      this.toDraw.add(inst);
    }
    for (const inst of this.toDraw) inst.view.render(this.driveOf(inst, t));
    this.toDraw.clear();
    this.wake();
    return moving;
  }

  /**
   * 时刻 t 的驱动量：位置、速度、加速度、各项加速度、手的位置、谁在带。只读状态，不改；绑定和演示的叠加层都用它。
   * 手带着时速度和加速度按 t 再算（指针停住也会降下来）；其余时候加速度是各项之和。
   */
  driveOf<V extends Value>(target: Instance<V>, t: number): Drive<V> {
    return this.drive(target as Instance, t) as Drive<Value> as Drive<V>;
  }

  private drive(inst: Instance, t: number): Drive<Value> {
    const ch = inst.ch;
    const base = { x: ch.x, hand: ch.hand, mode: inst.mode, segment: inst.segment };
    // 手只是放置点：力场带向手，加速度是算好的轨迹在这一步速度的变化 ÷ 步长，只有一项
    if (inst.holds() && ch.hand !== null && inst.def.channel.handIsPlacement) {
      const a = ch.motion ? ch.motion.m.at(ch.lastT - ch.motion.t0).a : inst.zero;
      return { ...base, v: ch.v, a, forces: [{ name: "力场", a }], driver: "手" };
    }
    if (inst.holds() && ch.hand !== null) {
      const k = this.kinematics(inst, Math.max(t, ch.lastT));
      const frame = inst.geometryFrame();
      return { ...base, v: k.v, a: k.a, forces: frame ? this.handForces(inst, frame, ch.x, k.v, k.a) : [], driver: "手" };
    }
    const mo = ch.motion;
    // 带向终态：加速度是算好的轨迹在这一步速度的变化 ÷ 步长（场可以给位置），只有一项
    if (mo && (mo.m.place !== null || !mo.frame)) {
      const a = mo.m.at(ch.lastT - mo.t0).a;
      return { ...base, v: ch.v, a, forces: [{ name: "力场", a }], driver: "力场" };
    }
    // 按力场推进：力场在当前状态给的加速度，只有一项（特例点的吸能不是场）
    if (mo && mo.frame) {
      const field = fieldForce(inst.def.channel, mo.frame, inst.params, inst.def.channel.field, { x: ch.x, v: ch.v }, { t: (ch.lastT - mo.t0) / 1000, start: mo.m.at(0) });
      return { ...base, v: ch.v, a: field.a, forces: [field], driver: "力场" };
    }
    return { ...base, v: inst.zero, a: inst.zero, forces: [], driver: "停住" };
  }

  // ===== 日志行 =====

  private sourceLine(change: InputChange): string {
    const r = change.raw;
    if (r.type === "time") return `${change.source} @ ${change.t.toFixed(1)} ms → 原语变化：${changeLabel(change.change)}（没有浏览器事件）`;
    const at = "x" in r ? ` (${r.x.toFixed(0)}, ${r.y.toFixed(0)})` : "";
    const extra = r.type === "pointer" ? ` buttons=${r.buttons}` : r.type === "key" ? ` code=${r.code} key=${r.key}${r.repeat ? " repeat" : ""}` : r.type === "wheel" ? ` deltaY=${r.dy} deltaX=${r.dx}` : "";
    return `浏览器事件：${change.source}${at}${extra} @ ${change.t.toFixed(1)} ms → 原语变化：${changeLabel(change.change)}`;
  }

  /** 输入层的导出量按读它的组件的参数表算。 */
  private inputLines(change: InputChange, params: ParamTable): string[] {
    const c = change.change;
    const lines: string[] = [];
    if (c.kind === "level" && c.corrected) lines.push("校正：此前发生过输入丢失，这次观测到的电平和记录不同，按实际值补出这一变化");
    if (c.kind === "level" && c.id.startsWith("pointer.") && c.edge === "↑") {
      const click = this.input.clickCount(c.id, params);
      if (click) lines.push(`输入层：${click.explain}`);
    }
    if (c.kind === "level" && c.edge === "↓") {
      const p = this.input.lastPress(c.id);
      if (p) lines.push(`输入层：按住时长 ${(change.t - p.t).toFixed(0)} ms`);
    }
    if (c.kind === "repeat" && c.id !== HOLD_REPEAT) lines.push(`输入层：自动重复脉冲，节奏由系统决定（${cite(params.keyboardDelay)}；${cite(params.keyboardRate)}）`);
    if (c.kind === "continuous" && c.id === "wheel") {
      lines.push(`输入层：增量 (${c.delta.x.toFixed(1)}, ${c.delta.y.toFixed(1)}) px，累计 (${this.input.wheelAmount().x.toFixed(0)}, ${this.input.wheelAmount().y.toFixed(0)}) px；对照 ${cite(params.wheelLines)}`);
    }
    return lines;
  }
}

export { POINTER_BUTTONS };
export type { LossId };
