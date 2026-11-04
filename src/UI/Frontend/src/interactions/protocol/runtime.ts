// Route input, evaluate transitions, emit outputs, and advance channel motion.
// Inject presentation, hosts, and platform operations without DOM dependencies.
// Execution mode selects original, optimized, or compared geometry and motion queries.

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

/** Current motion driver: hand, field, or rest. */
export type Driver = "手" | "力场" | "停住";

/** Frame drivers mapped by presentation to motion, shape, and color; values are scalar or plane-valued. */
export interface Drive<V extends Value = number> {
  /** Channel position, corresponding to the logical center when geometry exists. */
  x: V;
  /** Channel velocity in value units per second. */
  v: V;
  /** Total acceleration in value units per second squared; direct hand contribution is total minus field contributions. */
  a: V;
  /** Current hand and field acceleration contributions. */
  forces: readonly Force<V>[];
  /** Hand position while held, otherwise null; direct hand control matches the object within bounds. */
  hand: V | null;
  driver: Driver;
  mode: Mode;
  /** Output point reached by stationary release while held; null when no output point would be reached. */
  segment: V | null;
}

/** Component element, rendering, and geometry; shared platform operations are supplied separately. */
export interface Presentation<V extends Value = number> {
  element: unknown;
  /** Channel binding description, such as displacement, width, or color. */
  bindingLabel: string;
  /** Viewport centers at channel bounds, or null without screen geometry. */
  geometry(): Geometry | null;
  /** Let browser-owned controls bypass all routing. Only the innermost containing component is queried; the component own control is excluded. */
  nativeControl?(target: unknown): boolean;
  /** Draw frame drivers; place and scale around the logical center when geometry exists. */
  render(d: Drive<V>): void;
  /** Write accessible state and return the writes for tracing. */
  aria(hostValue: V): string;
}

/** Shared element operations supplied by the platform. */
export interface Platform {
  /** Whether the element contains the event target. */
  contains(element: unknown, target: unknown): boolean;
  /** Element bounds in viewport pixels. */
  rect(element: unknown): Rect | null;
  capture(element: unknown, pointerId: number): void;
  release(element: unknown, pointerId: number): void;
  focus(element: unknown): void;
  /** Pointer response area in viewport pixels. */
  responseZone(): Rect;
  /** Request at most one callback between frames; the host calls frame at the next frame time. */
  requestFrame(): void;
}

/** The host alone owns the value; components invoke its ports. Values match the channel shape. */
export interface Host<V extends Value = number> {
  value(): V;
  /** False disables routing while preserving motion toward the host value. */
  enabled(): boolean;
  call(port: string, args: readonly unknown[], cause: string): void;
  /** Subscribe to host changes; return cleanup for instance removal. */
  subscribe(listener: (old: V, next: V, cause: string) => void): () => void;
}

/** Channel position, velocity, and current driver. */
interface ChannelState<V extends Value> {
  x: V;
  v: V;
  /** Starting position captured on entering pointer ownership. */
  origin: V;
  /** Press hand anchor plus pointer displacement determines hand position; jump-to-pointer anchors at the pointer. */
  handStart: V;
  /** Hand position while held, otherwise null. */
  hand: V | null;
  lastT: number;
  /** Object samples since pointer ownership began, used to estimate hand-driven velocity. */
  samples: { t: number; x: V }[];
  /** Active segment, start time, drawn frame count, and initial geometry conversion. since preserves continuous field-motion time across retargeting until rest or direct hand control. */
  motion: { m: FieldMotion<V>; t0: number; frames: number; frame: Frame | null; since: number } | null;
  /** Pressed geometry and conversion cached during pointer ownership, otherwise null. */
  gesture: { geometry: Geometry; frame: Frame } | null;
}

export class Instance<V extends Value = Value> {
  mode: Mode = REST;
  segment: V | null = null;
  readonly ch: ChannelState<V>;
  /** Instance-level parameter overrides, changed through Runtime.setOverride. */
  readonly overrides: ParamOverrides = {};
  /** Complete resolved parameter table, recalculated when applicable layers change. */
  params!: ParamTable;

  readonly name: string;
  readonly def: Definition<V>;
  readonly view: Presentation<V>;
  readonly host: Host<V>;
  /** Whether the channel has two axes. */
  readonly plane: boolean;
  /** Zero matching the channel value shape. */
  readonly zero: V;
  /** Motion and geometry queries selected by execution mode. */
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

  /** Whether the mode is hand-driven. */
  holds(mode: Mode = this.mode): boolean {
    return this.def.channel.hand.includes(mode);
  }

  exclusive(mode: Mode = this.mode): boolean {
    return this.def.exclusive.includes(mode);
  }

  /** A mode reads primitives mentioned by its rows; exclusive modes additionally receive all pointer changes and input loss. */
  reads(change: InputChange): boolean {
    const c = change.change;
    if (readsOf(this.def, this.mode).has(c.id)) return true;
    return this.exclusive() && ((c.id.startsWith("pointer.") && c.id !== HOLD_REPEAT) || c.kind === "event");
  }

  /** Query reported geometry, or null when absent. */
  geometry(): Geometry | null {
    return this.q.geometry(() => this.view.geometry(), this.ch.gesture?.geometry ?? null);
  }

  /** Geometry conversion, reusing the pressed conversion when geometry is unchanged. */
  geometryFrame(): Frame | null {
    const g = this.geometry();
    if (!g) return null;
    return g === this.ch.gesture?.geometry ? this.ch.gesture.frame : frameOf(this.def.channel, g);
  }

  /** Convert a vector to the channel value shape. */
  value(v: { x: number; y: number }): V {
    return valueOfVec(this.plane, v) as V;
  }
}

/** Format values or literals as call arguments in traces. */
const argValue = (a: unknown) => (typeof a === "number" || (typeof a === "object" && a !== null && "x" in a) ? fmt(a as Value) : String(a));
const unitOf = (inst: Instance) => inst.def.channel.unit || "单位";
const tag = (inst: Instance) => `通道 ${inst.def.channel.label}`;
const jumps = (row: Row) => (row.out ?? []).some((o) => o.tag === "动画" && o.signal === "跳到指针处");
type StopOut = Extract<Output, { tag: "停住点" }>;
const stopOf = (row: Row) => (row.out ?? []).find((o): o is StopOut => o.tag === "停住点") ?? null;
const forceLine = (forces: readonly Force<Value>[], unit: string) =>
  (forces.length ? `各项加速度：${forces.map((f) => `${f.name} ${fmt(f.a)}`).join("、")} ${unit}/s²（相加 = 加速度）` : "各项加速度：无");

/** A group supplies children and region routing without modes or input reads; hosts own business mapping and views own visuals. */
export interface Composite {
  name: string;
  element: unknown;
  children: readonly Instance[];
  /** Group overrides apply to descendants; groups have no definition values. */
  readonly overrides: ParamOverrides;
  /** Route pointer changes inside the group but outside children; return a child and reason, or null. */
  route(pos: { x: number; y: number }): { child: Instance; why: string } | null;
}

/** Component instance or group forming a nested parameter scope. */
export type Part = Instance | Composite;

/** Parameter layers from global to local; source values are represented separately. */
export interface ConfigLayer extends ParamLayer {
  kind: "全局" | "母部件" | "本部件";
  /** Ancestor distance: zero for self, one for parent; null for global. */
  distance: number | null;
  /** Part represented by the layer, or null for global. */
  part: Part | null;
}

/** Layer view and resolved parameters; parameter source indices address this layer list. */
export interface ConfigView {
  /** Selected part, or null for the global view. */
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
  /** Global overrides at the broadest scope. */
  private readonly globalOverrides: ParamOverrides = {};
  private animation: AnimationMode = "normal";
  private holder: { inst: Instance; since: number; pointerId: number } | null = null;
  /** Pointer repeat schedule while an exclusive, left-held mode reads it. Preserve timing between reading modes; clear it otherwise. */
  private holdRepeat: { inst: Instance; mode: Mode; since: number; next: number; n: number } | null = null;
  /** Component containing the previous position-change target, used for leave detection. */
  private hovered = new Set<Instance>();
  private lastPointerId = 1;
  /** Use the current input time for synchronous host changes to keep one event clock. */
  private clock: number | null = null;
  /** Recheck instance drag thresholds after parameter changes once checks have been enabled. */
  private checked = false;
  /** Dirty instances draw once per frame; input and host changes update state without layout-invalidating writes. */
  private readonly toDraw = new Set<Instance>();
  /** Host subscription cleanup for each instance. */
  private readonly unsubscribe = new Map<Instance, () => void>();
  /** Whether a requested frame is still pending. */
  private frameRequested = false;

  private readonly trace: Trace;
  private readonly platform: Platform;
  private readonly exec: Execution;

  /** Execution mode, defaulting to original. */
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
    // Adding a part affects only its own parameter scope and descendants.
    this.resolveWithin(inst as unknown as Instance);
    this.unsubscribe.set(inst as unknown as Instance, host.subscribe((old, next, cause) => this.hostChanged(inst as Instance, old, next, cause)));
    view.aria(host.value());
    this.toDraw.add(inst);
    this.wake();
    return inst;
  }

  addComposite(c: Composite): void {
    this.composites.push(c);
    this.resolveWithin(c);
  }

  /** Remove routing, subscriptions, motion, repeat, and pointer capture without host calls. Recalculate descendants whose scope changed; repeated removal is harmless. */
  remove(part: Part, t: number): void {
    if (!(part instanceof Instance)) {
      const at = this.composites.indexOf(part);
      if (at < 0) return;
      this.composites.splice(at, 1);
      this.resolveWithin(part);
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
    this.resolveWithin(part);
    if (what.length) this.trace.write({ t, kind: "移除", component: part.name, summary: `移除 ${part.name}：结束了${what.join("、")}`, lines: ended });
  }

  /** Check nested drag boxes and conflicting declarations across mounted instances; use element containment and recheck changed drag parameters. */
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

  /** Trace comparison failures and notify the execution callback. */
  private mismatch(component: string, m: Mismatch): void {
    this.trace.write({ t: this.clock ?? performance.now(), kind: "错误", component, summary: `对照：${component} 的查询「${m.query}」两边不同`, lines: [m.what, `相同的条件：${m.same}`] });
    this.exec.onMismatch?.({ ...m, component });
  }

  // Parameter layers cascade from global through ancestors to the component.

  private elementOf(part: Part): unknown {
    return part instanceof Instance ? part.view.element : part.element;
  }

  /** Containing parts ordered from outermost to nearest. */
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

  /** Global, ancestor, and local layers from broadest to narrowest. */
  private layersOf(part: Part): ParamLayer[] {
    return [globalLayer(this.globalOverrides), ...this.parentsOf(part).map((p) => this.layerOf(p)), this.layerOf(part)];
  }

  /** Declarations from these parts and descendants; own declarations precede descendants, then registration order breaks ties. */
  private specsOf(parts: readonly Part[]): ParamSpec[] {
    const inside = this.instances.filter((inst) => parts.some((p) => p === inst || this.parentsOf(inst).includes(p)));
    const ordered = [...inside.filter((inst) => parts.includes(inst)), ...inside.filter((inst) => !parts.includes(inst))];
    const seen = new Set<string>();
    const specs: ParamSpec[] = [];
    for (const inst of ordered) for (const p of declaredSpecs(inst.def)) if (!seen.has(p.id) && seen.add(p.id)) specs.push(p);
    return specs;
  }

  private resolveWithin(part: Part): void {
    for (const inst of this.governedBy(part)) inst.params = resolveParams(this.system, this.layersOf(inst), declaredSpecs(inst.def));
  }

  /** Global-only table including declarations from every instance. */
  globalParams(): ParamTable {
    return resolveParams(this.system, [globalLayer(this.globalOverrides)], this.specsOf([...this.instances, ...this.composites]));
  }

  /** Preorder part tree with ancestor depth for the parameter selector. */
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

  /** Parameter view for a part and its descendants, or all instances for global; layers run from broadest to narrowest. */
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

  /** Set one layer override; null value removes it and null part selects global. Recalculate only affected instances and trace winners. */
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

  /** Replace an entire override layer, removing absent entries and recalculating affected instances once. */
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

  /** Replace system readings; null denotes read failure. Recalculate all instances once. */
  setSystem(system: SystemReadings | null, t: number): void {
    this.system = system;
    const read = system ? Object.entries(system).map(([k, v]) => `${k} ${v ?? "读取失败"}`).join("；") : "全部读取失败，系统项用默认值";
    const diffs = this.reresolve(this.instances, null);
    this.trace.write({ t, kind: "参数", component: "", summary: `参数：系统读数${system ? "更新" : "读取失败"}`, lines: [`系统：${read}`, ...(diffs.length ? diffs : ["各部件读到的值都没变"])] });
    if (this.checked) this.checkInstances(t);
  }

  /** Recalculate selected instances and report changed values; null ids checks every parameter. */
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

  /** Instances in a part scope include itself and descendants; global includes all. */
  private governedBy(part: Part | null): Instance[] {
    if (!part) return this.instances;
    const element = this.elementOf(part);
    return this.instances.filter((inst) => inst === part ||
      (inst.view.element !== element && this.platform.contains(element, inst.view.element)));
  }

  setAnimationMode(mode: AnimationMode, t: number): void {
    this.animation = mode;
    this.trace.write({ t, kind: "参数", component: "", summary: `动画档位：${ANIMATION_LABELS[mode]}`, lines: [
      mode === "normal" ? "手放开后按力场推进和到终态逐帧走" : "按力场推进和到终态直接到终点，状态变了当场就画、不等下一帧；手带着时位置仍是手的位置",
      "档位只影响动画输出；业务逻辑输出和框架输出不变，落点照样按力场算",
    ] });
    // Disabled animation modes finish active motion immediately.
    if (mode !== "normal") {
      for (const inst of this.instances) {
        if (!inst.ch.motion) continue;
        this.advance(inst, t);
        this.finishMotion(inst, t);
        this.toDraw.add(inst);
      }
    }
    this.wake(t);
  }

  feed(raw: RawInput): void {
    // Emit scheduled repeats before processing later input, preserving time order.
    this.pumpHoldRepeat(raw.t);
    if (raw.type === "pointer") this.lastPointerId = raw.pointerId;
    this.clock = raw.t;
    try {
      this.input.feed(raw, (change) => this.route(change));
    } finally {
      this.clock = null;
    }
    this.wake(raw.t);
  }

  // Input routing.

  /** Containing components and groups from innermost to outermost. */
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
    // Browser-owned controls bypass both inner and outer components.
    const innermost = chain.find((n): n is Instance => n instanceof Instance);
    if (innermost?.view.nativeControl?.(raw.target)) {
      if (c.key !== "pointer.position~") this.unread(change, innermost.name, "原生控件", `事件目标是 ${innermost.name} 里交给浏览器的原生控件（呈现层声明，协议第 4.2 节第 2 项）`);
      return;
    }
    if (chain.length === 0) {
      // Skip unowned pointer/key trace noise; retain requests and input-loss events.
      if (c.kind === "request" || c.kind === "event") this.unread(change, "", "没有组件", "事件目标不在任何组件内");
      return;
    }
    // Ask from inner to outer; the first enabled reader evaluates the change.
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
      // A child containing the target was already queried; do not search group regions again.
      if (!pointerish || node.children.some((child) => this.platform.contains(child.view.element, raw.target))) continue;
      const r = node.route(this.input.position());
      if (!r) { asked.push(`${node.name} 不把这里交给子组件`); continue; }
      const via = `区域路由：落在${node.name}的空白处，${r.why}`;
      // Treat routing to an already-removed child as no route.
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

  /** Deliver derived leave before routing the current change when the previous component is no longer in the target chain. Pointer ownership bypasses leave detection. */
  private leave(change: InputChange, chain: readonly (Instance | Composite)[]): void {
    const now = new Set(chain.filter((n): n is Instance => n instanceof Instance));
    const left = [...this.hovered].filter((inst) => !now.has(inst));
    this.hovered = now;
    for (const inst of left) {
      const lc: InputChange = { ...change, change: { kind: "event", id: POINTER_LEAVE, key: POINTER_LEAVE } };
      if (inst.host.enabled() && inst.reads(lc)) this.evaluate(inst, lc, chain.length === 0 && change.change.id === "loss.leftzone" ? `指针离开：指针出了响应区，不在 ${inst.name} 内了` : `指针离开：事件目标不在 ${inst.name} 内了`);
    }
  }

  /** Unread input keeps normal browser behavior. */
  private unread(change: InputChange, component: string, who: string, why: string): void {
    const label = changeLabel(change.change);
    this.trace.write({
      t: change.t, kind: "未读取", component,
      summary: `${label} → ${who}，交给浏览器`,
      lines: [this.sourceLine(change), why],
      group: `unread:${component}:${who}:${change.change.key}`,
    });
  }

  // Transition evaluation.

  /** Context for facts and value functions. */
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

  /** Most recent mapped hand position and velocity while held, otherwise null. */
  private handState(inst: Instance, t: number): { x: Value; v: Value } | null {
    const ch = inst.ch;
    if (ch.hand === null || !ch.samples.length) return null;
    return { x: ch.samples[ch.samples.length - 1].x, v: this.kinematics(inst, t).v };
  }

  /** Whether the last left press targeted this component. */
  private pressedOn(inst: Instance): boolean {
    const p = this.input.lastPress("pointer.left");
    return p !== null && p.target !== null && this.platform.contains(inst.view.element, p.target);
  }

  /** Return true when handled, unread, or errored. A declined row preserves state and returns a reason so routing continues outward. */
  private evaluate(inst: Instance, change: InputChange, routeWhy: string): true | string {
    const c = change.change;
    const t = change.t;
    if (!inst.reads(change)) {
      if (c.key !== "pointer.position~") this.unread(change, inst.name, `${inst.name}（${inst.mode}）不读`, `${routeWhy}；${inst.mode} 模式的读取里没有它`);
      return true;
    }
    const before = { mode: inst.mode, segment: inst.segment };
    const lines: string[] = [this.sourceLine(change), ...this.inputLines(change, inst.params), `路由：${routeWhy}`, `当前：模式 ${before.mode}${before.segment !== null ? `，所在段 ${fmt(before.segment)}` : ""}`];

    // Advance active field motion to input time before facts or values read it.
    this.advance(inst, t);

    // Update hand state before transition evaluation.
    // Update even on release so a stationary pointer yields the release-time velocity.
    let handDone = false;
    if (inst.holds()) {
      lines.push(...this.dragUpdate(inst, t));
      handDone = true;
    }

    // Evaluate facts and select the unique row.
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

    // Entering pointer ownership stops prior motion, captures start anchors, and updates hand state when needed.
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
      // Cache gesture geometry until pointer ownership ends.
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

    // On release, absorb configured energy and predict landing when the stop point is landing or absent.
    // Other stop values become terminal field targets.
    const stop = stopOf(row);
    const byField = letGo && (stop === null || ("value" in stop.value && stop.value.value === LANDING));
    let motion: FieldMotion | null = null;
    const motionFrame = byField ? inst.geometryFrame() : null;
    // The release row exposes the final hand state to values and fields.
    const letGoHand = letGo ? this.handState(inst, t) : null;
    if (letGo) lines.push(this.letGo(inst, t));
    if (byField) {
      motion = inst.q.motion(inst.def.channel, motionFrame, inst.params, inst.def.channel.field, inst.ch.x, inst.ch.v, null, { letGo: letGoHand, moving: this.movingFor(inst, t) });
      lines.push(...motion.lines.map((s) => `${tag(inst)}：${s}`));
    }

    // Resolve stop points and arguments after hand updates or release prediction.
    // Substitute the stop value into the channel stop call.
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

    // Change mode and clear the release segment: landing follows the field, current stops immediately, other values target a terminal.
    inst.mode = next;
    if (letGo) {
      if (inst.segment !== null) lines.push(`所在段：手放开，所在段 ${fmt(inst.segment)} → 无`);
      inst.segment = null;
    }
    if (motion) lines.push(...this.startFree(inst, motion, motionFrame, t));
    else if (stop && stopValue) lines.push(...this.goStop(inst, stop.value, stopValue, t, letGoHand));
    if (before.mode !== inst.mode) lines.push(`转移：模式 ${before.mode} → ${inst.mode}`);

    // Apply framework claim/focus and pointer capture changes before business calls.
    // Trace mode/segment changes and jump-to-pointer output.
    const outLines: string[] = [];
    // Declined rows already returned; only claim and focus outputs remain here.
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
    // Pointer routing ownership follows exclusive-mode entry and exit.
    if (inst.exclusive() && this.holder?.inst !== inst) this.holder = { inst, since: entry.seq, pointerId: this.lastPointerId };
    if (!inst.exclusive() && this.holder?.inst === inst) this.holder = null;
    this.syncHoldRepeat(inst, t, before.mode);
    for (const call of calls) inst.host.call(call.port, call.args, `${inst.name} 的调用（日志 #${entry.seq}）`);
    // After calls, reconcile the expected stop value against the host when no hand controls the channel.
    // Synchronous host changes may already have reconciled; retarget only if still different.
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

  // Channel hand and field motion.

  /** Direct hand control follows mapped bounded position. Targeted hand control preserves object state and follows the field. The segment is its stationary-release output point. */
  private dragUpdate(inst: Instance, t: number): string[] {
    const frame = inst.geometryFrame();
    if (!frame) return [`${tag(inst)}：没有几何，手带不动`];
    const ch = inst.ch;
    const hand = handPosition(inst.def.channel, ch.handStart, this.input, frame);
    const held = holdAt(inst.def.channel, hand.hand, inst.params);
    const placement = inst.def.channel.handIsPlacement === true;
    let towardLines: string[] = [];
    if (placement) {
      // Advance before retargeting the field to the hand; sample mapped hand positions for hand velocity.
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

  /** Direct hand acceleration is total acceleration minus the field contribution. */
  private handForces(inst: Instance, frame: Frame, x: Value, v: Value, a: Value): Force<Value>[] {
    const field = fieldForce(inst.def.channel, frame, inst.params, inst.def.channel.field, { x, v });
    return [{ name: "手", a: inst.value(sub(vec(a), vec(field.a))) }, field];
  }

  /** Derive hand velocity and acceleration from position samples since press; reread at current time so stationary input decays to zero. */
  private kinematics(inst: Instance, t: number): { v: Value; a: Value; explain: string } {
    const W = inst.params.velocityWindow;
    let press = -Infinity;
    for (const b of POINTER_BUTTONS) {
      const p = this.input.lastPress(b.id);
      if (p && p.t > press) press = p.t;
    }
    // Use the same velocity window for both plane components.
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

  /** Release absorbs kinetic energy by scaling velocity by sqrt(1-r). Field-driven hand release preserves object velocity, not hand velocity. */
  private letGo(inst: Instance, t: number): string {
    this.advance(inst, t);
    const r = inst.params.releaseAbsorb;
    const v = inst.ch.v;
    inst.ch.v = inst.value(scale(vec(v), keep(r.value)));
    inst.ch.hand = null;
    return `${tag(inst)}：手放开，速度 ${fmt(v)} → ${fmt(inst.ch.v)} ${unitOf(inst)}/s（${cite(r)}，速度乘 √(1 − ${r.value})）`;
  }

  /** Active field-motion description, or null at rest. */
  private moving(inst: Instance): string | null {
    const m = inst.ch.motion;
    if (!m) return null;
    return m.m.place === null ? "按力场推进" : `带向${m.m.place.toward === "手" ? "手" : "终态"}`;
  }

  /** Continuous field-motion duration in seconds, or zero at rest. */
  private movingFor(inst: Instance, t: number): number {
    const m = inst.ch.motion;
    return m ? (t - m.since) / 1000 : 0;
  }

  /** Retarget from current state and preserve continuous motion time while moving. A stationary hand target remains hand-controlled. */
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

  /** Current stops immediately at zero velocity; other stop values use terminal motion. A rejecting host is reconciled after calls. */
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

  /** Start untargeted field motion, preserving continuous motion time; snap when already at rest, without geometry, or with animation disabled. */
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

  /** Finish at the computed stop. Reconcile free motion with the host; report a missed terminal target without retrying it. */
  private finishMotion(inst: Instance, t: number): void {
    const ch = inst.ch;
    const mo = ch.motion!;
    ch.motion = null;
    ch.x = mo.m.rest;
    ch.v = inst.zero;
    ch.lastT = t;
    const place = mo.m.place;
    // After reaching the hand, retain hand control at the field stop.
    if (place?.toward === "手") return;
    const took = `用时 ${(t - mo.t0).toFixed(0)} ms、${mo.frames} 帧（推进本身 ${msText(mo.m.durationMs)} ms）`;
    // Write deferred final traces only when an on-demand segment ends.
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

  /** Expected rounded stop value: terminal target, predicted free-motion landing, or current position. */
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

  /** Without hand control, target the host value when it differs from the expected stop; return trace lines. */
  private reconcile(inst: Instance, t: number, why: string): string[] {
    if (inst.holds()) return [];
    this.advance(inst, t);
    const target = inst.host.value();
    const will = this.willRest(inst);
    if (sameValue(will.value, target)) return [];
    return this.startCarry(inst, target, t, `${why}：宿主值 ${fmt(target)}，物体将停在${will.text}，不同`, null);
  }

  /** Carry current position/velocity to terminal to. Disabled animation snaps; release-started motion receives the final hand state. */
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
    this.wake(t);
  }

  /** Read segment position and velocity at time t. */
  private advance(inst: Instance, t: number): void {
    const ch = inst.ch;
    const run = ch.motion;
    // For late events, read motion at event time instead of advancing from the later rendered state.
    if (t <= ch.lastT && !(run && t >= run.t0 && t < ch.lastT)) return;
    if (run) {
      const s = run.m.at(t - run.t0);
      ch.x = s.x;
      ch.v = s.v;
    }
    ch.lastT = t;
  }

  /** Run repeat only while exclusive, left-held, and in a reading mode. Preserve timing between reading modes; otherwise start or end the schedule. */
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

  /** Emit all repeats due by t in scheduled order at their own timestamps. */
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

  /** Frames are needed for field motion, dirty instances, or a pending repeat. */
  private needsFrame(): boolean {
    return this.toDraw.size > 0 || this.holdRepeat !== null || this.instances.some((inst) => inst.ch.motion !== null);
  }

  /** Request one pending frame. Disabled animation draws terminal state immediately in the current input or host update. */
  private wake(t?: number): void {
    if (this.animation !== "normal" && this.toDraw.size > 0) {
      const now = t ?? this.clock ?? performance.now();
      for (const inst of this.toDraw) inst.view.render(this.driveOf(inst, now));
      this.toDraw.clear();
    }
    if (this.frameRequested || !this.needsFrame()) return;
    this.frameRequested = true;
    this.platform.requestFrame();
  }

  /** Per frame, emit repeats, advance field motion, and draw dirty instances once; request another frame if needed. */
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

  /** Read frame drivers without changing state. Recompute held velocity/acceleration at t so stationary pointers decay; other acceleration is the contribution sum. */
  driveOf<V extends Value>(target: Instance<V>, t: number): Drive<V> {
    return this.drive(target as Instance, t) as Drive<Value> as Drive<V>;
  }

  private drive(inst: Instance, t: number): Drive<Value> {
    const ch = inst.ch;
    const base = { x: this.shown(inst), hand: ch.hand, mode: inst.mode, segment: inst.segment };
    // Hand-targeted motion uses trajectory velocity differences as its sole acceleration contribution.
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
    // Terminal motion uses trajectory velocity differences, including position-supplying fields.
    if (mo && (mo.m.place !== null || !mo.frame)) {
      const a = mo.m.at(ch.lastT - mo.t0).a;
      return { ...base, v: ch.v, a, forces: [{ name: "力场", a }], driver: "力场" };
    }
    // Free motion reads field acceleration; special-point absorption is excluded.
    if (mo && mo.frame) {
      const field = fieldForce(inst.def.channel, mo.frame, inst.params, inst.def.channel.field, { x: ch.x, v: ch.v }, { t: (ch.lastT - mo.t0) / 1000, start: mo.m.at(0) });
      return { ...base, v: ch.v, a: field.a, forces: [field], driver: "力场" };
    }
    return { ...base, v: inst.zero, a: inst.zero, forces: [], driver: "停住" };
  }

  /** Quantize field-driven drawing into n steps without changing continuous state, capture, landing, or host values. Direct hand motion and n=0 draw continuously. */
  private shown(inst: Instance): Value {
    const ch = inst.ch;
    const mo = ch.motion;
    const steps = inst.params.outputSteps.value;
    if (!mo || inst.holds() || !(steps > 0)) return ch.x;
    const span = mo.m.durationMs / steps;
    if (!(span > 0)) return ch.x;
    return mo.m.at(Math.floor((ch.lastT - mo.t0) / span) * span).x;
  }



  private sourceLine(change: InputChange): string {
    const r = change.raw;
    if (r.type === "time") return `${change.source} @ ${change.t.toFixed(1)} ms → 原语变化：${changeLabel(change.change)}（没有浏览器事件）`;
    const at = "x" in r ? ` (${r.x.toFixed(0)}, ${r.y.toFixed(0)})` : "";
    const extra = r.type === "pointer" ? ` buttons=${r.buttons}` : r.type === "key" ? ` code=${r.code} key=${r.key}${r.repeat ? " repeat" : ""}` : r.type === "wheel" ? ` deltaY=${r.dy} deltaX=${r.dx}` : "";
    return `浏览器事件：${change.source}${at}${extra} @ ${change.t.toFixed(1)} ms → 原语变化：${changeLabel(change.change)}`;
  }

  /** Derive input values using the reading component parameter table. */
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
