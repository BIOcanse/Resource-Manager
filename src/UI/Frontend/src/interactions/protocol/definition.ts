// 组件定义（协议第 4.1 节）：模式、转移表、取值、通道。全部是声明。
// 每个模式读哪些输入由它的行决定：哪个原语在这个模式的行里出现，这个模式就读它的全部变化。

import type { Field } from "./field.ts";
import type { InputLayer } from "./input.ts";
import type { ParamOverrides, ParamSpec, ParamTable } from "./params.ts";
import type { Value } from "./vec.ts";

export type Mode = string;
export const REST: Mode = "静止";

/** 守卫只读命名事实（真假值）。事实的算法和边界只在 facts.ts 写一次。 */
export type FactId = "离开拖动框" | "指针在组件内" | "连按≥2" | "Shift 按住" | "可拖" | "在组件上按住" | "沿运动方向" | "手过了最大值";

/** 业务调用：端口和参数。 */
export interface Call { tag: "业务"; port: string; args: readonly Arg[] }

/**
 * 行上写的输出。捕获和释放指针不写在行上：进入独占模式时运行时捕获，离开时释放（第 4.1 节第 2 项）。
 * 模式和所在段也不写：它们是驱动量，每帧交给绑定，变化时日志记一条。手放开后按力场推进，也不用写（第 4.5 节）。
 * 停住点：物体停在哪（一个取值或字面量）。运行时按通道的“停住点的调用”调用，物体按它去那里（协议第 4.1 节第 4 项）。
 */
export type Output =
  | Call
  | { tag: "停住点"; value: Arg }
  /** 交给外层：这个组件不收这一格的变化，照不读处理，问外一层（协议第 4.2 节）。行上模式不变、没有别的输出。 */
  | { tag: "框架"; act: "认领" | "焦点" | "交给外层" }
  | { tag: "动画"; signal: "跳到指针处" };

/** 运行时提供的取值：手放开后按力场推进停下的位置，按步长取整。只能写成手放开的行的停住点。 */
export const LANDING = "落点";
/** 运行时提供的取值：通道当前的位置按步长取整。 */
export const CURRENT = "当前值";
/** 运行时提供的取值：进入独占模式时记下的起点按步长取整（取消时调用回它）。 */
export const ORIGIN = "起点的值";
export const BUILTIN_VALUES: readonly string[] = [LANDING, CURRENT, ORIGIN];
/** 停住点的调用里代表停住点的参数：调用时换成行上写的停住点的值。只能写在停住点的调用里。 */
export const STOP_POINT = "停住点";

/** 调用参数：命名取值（组件定义里的，或运行时提供的三样），或字面量。 */
export type Arg = { value: string } | { literal: number | boolean | string };

export interface Row {
  from: readonly Mode[];
  /** 输入变化：`pointer.left↑`、`pointer.position~`、`key.Escape⟳`、`request.activate`、`loss.blur` 等。 */
  on: readonly string[];
  when?: Readonly<Partial<Record<FactId, boolean>>>;
  /** "=" 表示模式不变。 */
  to: Mode | "=";
  out?: readonly Output[];
  note?: string;
}

export interface Point { x: number; y: number }

/**
 * 呈现层报告的几何（协议第 4.1 节第 6 项）：值最小、最大时，部件的逻辑中心在屏幕上的位置（视口 px），中间按比例。
 * 线：两点的连线是运动方向。平面：两点是矩形的两个对角，x 轴对屏幕横向、y 轴对屏幕纵向。
 * 物理位移都按逻辑中心算。没有几何（例如只映射到颜色）就对应不到屏幕位置，没有逻辑中心，不能从位置反推值。
 */
export interface Geometry {
  /** 逻辑中心是部件上的哪一点，例如“圆球圆心”。 */
  center: string;
  /** 值为通道最小值时逻辑中心的屏幕位置。 */
  atMin: Point;
  /** 值为通道最大值时逻辑中心的屏幕位置。 */
  atMax: Point;
}

/**
 * 通道（协议第 4.5 节）：物体有位置、速度，由一个力场带着；放置点是输出映射点、手和终态。手默认绝对吸附（按住时位置 = 手）；不出边界。
 * 有几何时，通道的位置就是部件逻辑中心的位置，用值的单位表示。
 * V 是值的样子：一条轴（线）时是数；两条轴（平面）时是点 (x, y)，边界是矩形（协议第 4.5 节“平面”）。
 */
export interface ChannelDef<V extends Value = number> {
  id: string;
  label: string;
  unit: string;
  /** 边界（平面上是矩形的两个角）。 */
  min: V;
  max: V;
  /** 值 = 位置按步长取整（平面上每条轴各自的步长），只用于调用参数，画面不取整；没有就不取整。 */
  step?: V;
  /**
   * 输出映射点（值，用户定名，原来叫吸附点）：拖动中此刻静止松手会停到某个点，所在段就是这个点，触发所在段的输出映射。
   * 场可以读它们（例如吸附指向最近的点）；它们也是特例点，到达时吸掉“点吸能”比例的动能。
   */
  mappingPoints?: readonly V[];
  /** 哪些模式由手带（拖动的公共行里是“拖动”）。手放开时带不带速度由参数“松手吸能”决定。 */
  hand: readonly Mode[];
  /** 手的映射：手的位置 → 物体的位置。不写就是“不出边界”：位置 = 手的位置，越过边界停在边界上。 */
  handMap?: HandMap<V>;
  /**
   * 力场（engine/field.ts）：数学逻辑加放置点。放置点是输出映射点、手（手只是放置点时）和终态，场读物体被带向的是哪一个（没有时自己看输出映射点）。
   * 手放开后按它推进推出落点；宿主值和物体将要停的值不同时按它带向终态。边界和放置点上的吸能是特例点，不写在这里。
   */
  field: Field;
  /**
   * 手只是放置点（协议第 4.5 节，用户「跟着手的无非就是放置点跟手移动而已」）：由手带时手的位置（经手的映射、不出边界）是放置点，
   * 手每动一次，物体从当时的位置和速度出发由力场带向它。不写就是手绝对吸附：位置 = 手的位置（用户「手默认绝对刚性」）。
   */
  handIsPlacement?: boolean;
  /** 停住点的调用：行上写了停住点时调用它，参数里的“停住点”换成那个值，例如 select(停住点)。行上写停住点时要有。 */
  stopCall?: Call;
}

/**
 * 手的映射（协议第 4.5 节“谁在带物体”）：按住时物体的位置 = to(手的位置)，再不出边界。
 * from 是反向：按下时从物体当前的位置推出手所在处，按住正在动的物体时它不跳。例：橡皮筋（越过内容边缘后手走得越远，位置走得越少）。
 */
export interface HandMap<V extends Value = number> {
  name: string;
  to(hand: V, params: ParamTable): V;
  from(x: V, params: ParamTable): V;
  /** 映射和参数的关系，日志照写。 */
  describe(params: ParamTable): string;
  /** 自己声明的参数（协议第 5 节“场和取值自己的参数”）。 */
  declares?: readonly ParamSpec[];
}

/** 取值的结果：通道的值（数或点），日志照写怎么算的。 */
export interface ValueResult { value: Value; explain: string }

export interface PortDecl {
  name: string;
  params: readonly string[];
  continuous: boolean;
}

/** 取值的函数。写成方法的形状，线和平面的定义都能交给只认 Definition<Value> 的运行时。 */
export type ValueFn<V extends Value> = { f(ctx: ValueContext<V>): ValueResult }["f"];

export interface Definition<V extends Value = number> {
  id: string;
  name: string;
  modes: readonly Mode[];
  /** 这些模式独占指针：指针类变化和输入丢失事件都归它；进入时捕获指针，离开时释放。 */
  exclusive: readonly Mode[];
  /** 某模式下必然按住的电平，省掉不可能出现的 ↑。 */
  held?: Readonly<Record<Mode, readonly string[]>>;
  rows: readonly Row[];
  channel: ChannelDef<V>;
  ports: readonly PortDecl[];
  /** 组件自己的命名取值，供调用参数使用；运行时另外提供落点、当前值、起点的值。 */
  values: Readonly<Record<string, ValueFn<V>>>;
  /** 部件层参数覆盖：这种组件确实需要不同的项才写（协议第 5 节）。 */
  params?: ParamOverrides;
  /** 组件的取值自己声明的参数（协议第 5 节“场和取值自己的参数”）。 */
  declares?: readonly ParamSpec[];
}

/** 组件用到的力场、手的映射和取值声明的参数，按这个次序，全部列出（机器检查看有没有同名的不同声明）。 */
export function declarationsOf(def: Definition<Value>): ParamSpec[] {
  return [...(def.channel.field.declares ?? []), ...(def.channel.handMap?.declares ?? []), ...(def.declares ?? [])];
}

/** 组件的值表除了协议的参数，还有它声明的参数：同名的只取第一处（同名的不同声明由机器检查报出）。 */
export function declaredSpecs(def: Definition<Value>): ParamSpec[] {
  const seen = new Set<string>();
  return declarationsOf(def).filter((p) => !seen.has(p.id) && seen.add(p.id));
}

/** 输入变化所属的原语：去掉末尾的 ↑ ↓ ⟳ ~，例如 `key.Escape⟳` → `key.Escape`。 */
export const primitiveOf = (change: string): string => change.replace(/[↑↓⟳~]$/, "");

/** 每个模式读哪些原语：这个模式的行（含 from 写 “*” 的行）里出现过的。按定义缓存。 */
const readsCache = new WeakMap<Definition<Value>, Map<Mode, Set<string>>>();
export function readsOf(def: Definition<Value>, mode: Mode): ReadonlySet<string> {
  let byMode = readsCache.get(def);
  if (!byMode) {
    byMode = new Map();
    for (const m of def.modes) {
      const set = new Set<string>();
      for (const r of def.rows) if (r.from.includes(m) || r.from.includes("*")) for (const c of r.on) set.add(primitiveOf(c));
      byMode.set(m, set);
    }
    readsCache.set(def, byMode);
  }
  return byMode.get(mode) ?? new Set();
}

/** 取值和事实能读到的东西。V 是通道的值的样子（线是数，平面是点）。 */
export interface ValueContext<V extends Value = number> {
  t: number;
  input: InputLayer;
  /** 这个组件实例级联后的值表。 */
  params: ParamTable;
  hostValue: V;
  /**
   * 通道的位置、速度；手；进入独占模式时记下的起点；所在段。
   * 手：手带着物体去的位置（经过手的映射、在边界里）和速度（这个位置在速度窗口内的变化率，窗口不早于这次按下）。手带着时有，手放开的那一行读到放开那一刻的，别的时候是 null。
   * 手绝对吸附时就是物体的位置和速度；手只是放置点时物体落在手后面，两者不同。
   */
  channel: { x: V; v: V; hand: { x: V; v: V } | null; origin: V; segment: V | null };
  geometry: Geometry | null;
  def: Definition<V>;
}

export interface FactContext extends ValueContext<Value> {
  rect: { left: number; top: number; right: number; bottom: number } | null;
  /** 最近一次左键按下时，事件目标在不在这个组件的元素里（没有按下过为 false）。 */
  pressedOn: boolean;
  /** 手此刻的位置经手的映射、还没限在边界里（物体停在边界上时手可以在外面）；手不带着时为 null。 */
  handUnbounded: Value | null;
  /** 通道绑定到什么，例如“位移”“颜色”；日志照写。 */
  bindingLabel: string;
}
