// Component declarations: modes, transitions, values, and channels.
// A mode reads every change of each primitive mentioned in its rows.

import type { Field } from "./field.ts";
import type { InputLayer } from "./input.ts";
import type { ParamOverrides, ParamSpec, ParamTable } from "./params.ts";
import type { Value } from "./vec.ts";

export type Mode = string;
export const REST: Mode = "静止";

/** Guards read named boolean facts defined in facts.ts. */
export type FactId = "离开拖动框" | "指针在组件内" | "连按≥2" | "Shift 按住" | "可拖" | "在组件上按住" | "沿运动方向" | "手过了最大值";

/** Business call port and arguments. */
export interface Call { tag: "业务"; port: string; args: readonly Arg[] }

/** Row outputs. The runtime manages pointer capture, frame drivers, and field motion; stop points use the channel stop call. */
export type Output =
  | Call
  | { tag: "停住点"; value: Arg }
  /** Decline the input to the outer component without changing mode or emitting outputs. */
  | { tag: "框架"; act: "认领" | "焦点" | "交给外层" }
  | { tag: "动画"; signal: "跳到指针处" };

/** Runtime landing value after field motion and step rounding; valid only as a hand-release stop point. */
export const LANDING = "落点";
/** Current channel position rounded to its step. */
export const CURRENT = "当前值";
/** Starting value captured on pointer ownership, rounded to its step for cancellation. */
export const ORIGIN = "起点的值";
export const BUILTIN_VALUES: readonly string[] = [LANDING, CURRENT, ORIGIN];
/** Stop-call argument replaced by the row stop value; valid only in stop calls. */
export const STOP_POINT = "停住点";

/** Named component/runtime value or literal. */
export type Arg = { value: string } | { literal: number | boolean | string };

export interface Row {
  from: readonly Mode[];
  /** Input change such as pointer.left↑, key.Escape⟳, request.activate, or loss.blur. */
  on: readonly string[];
  when?: Readonly<Partial<Record<FactId, boolean>>>;
  /** "=" preserves the current mode. */
  to: Mode | "=";
  out?: readonly Output[];
  note?: string;
}

export interface Point { x: number; y: number }

/** Viewport-pixel centers at the channel bounds define linear geometry. A line follows the endpoints; a plane uses opposite rectangle corners. Without geometry, pointer positions cannot be inverted to values. */
export interface Geometry {
  /** Logical center description, such as the thumb center. */
  center: string;
  /** Viewport center at the minimum channel value. */
  atMin: Point;
  /** Viewport center at the maximum channel value. */
  atMax: Point;
}

/** A bounded channel carries position and velocity under a field. Values are scalar for a line or (x, y) for a plane. Direct hand control snaps to the mapped hand position by default. */
export interface ChannelDef<V extends Value = number> {
  id: string;
  label: string;
  unit: string;
  /** Channel bounds, or opposite rectangle corners for a plane. */
  min: V;
  max: V;
  /** Step rounding applies to call arguments only; drawing remains continuous. */
  step?: V;
  /** Named output mapping points select the current segment and absorb the configured fraction of kinetic energy on arrival. */
  mappingPoints?: readonly V[];
  /** Hand-driven modes; release energy absorption determines retained velocity. */
  hand: readonly Mode[];
  /** Optional hand-to-object map; the default follows the hand within bounds. */
  handMap?: HandMap<V>;
  /** Field combining motion math with output, hand, and terminal placement points. It predicts release landing and carries host-value changes to their terminal value. */
  field: Field;
  /** Use the mapped hand as a field target instead of direct attachment, preserving object position and velocity on retargeting. */
  handIsPlacement?: boolean;
  /** Call issued for row stop points, substituting the stop value into its stop argument. */
  stopCall?: Call;
}

/** Hand mapping with inverse from object position, preventing jumps when grabbing a moving object. */
export interface HandMap<V extends Value = number> {
  name: string;
  to(hand: V, params: ParamTable): V;
  from(x: V, params: ParamTable): V;
  /** Mapping and parameter description for traces. */
  describe(params: ParamTable): string;
  /** Parameters declared by this mapping. */
  declares?: readonly ParamSpec[];
}

/** Named value result and trace explanation. */
export interface ValueResult { value: Value; explain: string }

export interface PortDecl {
  name: string;
  params: readonly string[];
  continuous: boolean;
}

/** Method shape allows scalar and plane definitions in the shared runtime. */
export type ValueFn<V extends Value> = { f(ctx: ValueContext<V>): ValueResult }["f"];

export interface Definition<V extends Value = number> {
  id: string;
  name: string;
  modes: readonly Mode[];
  /** Pointer-exclusive modes capture on entry and release on exit; they receive all pointer changes and input loss. */
  exclusive: readonly Mode[];
  /** Levels guaranteed held in a mode, excluding impossible release changes. */
  held?: Readonly<Record<Mode, readonly string[]>>;
  rows: readonly Row[];
  channel: ChannelDef<V>;
  ports: readonly PortDecl[];
  /** Component named values, in addition to runtime landing/current/start values. */
  values: Readonly<Record<string, ValueFn<V>>>;
  /** Component-level parameter overrides. */
  params?: ParamOverrides;
  /** Parameters declared by component value functions. */
  declares?: readonly ParamSpec[];
}

/** Field, hand-map, and value declarations in that order; checks reject conflicting names. */
export function declarationsOf(def: Definition<Value>): ParamSpec[] {
  return [...(def.channel.field.declares ?? []), ...(def.channel.handMap?.declares ?? []), ...(def.declares ?? [])];
}

/** Built-in and declared parameters; keep the first declaration for each name. */
export function declaredSpecs(def: Definition<Value>): ParamSpec[] {
  const seen = new Set<string>();
  return declarationsOf(def).filter((p) => !seen.has(p.id) && seen.add(p.id));
}

/** Primitive owning a change, with its change suffix removed. */
export const primitiveOf = (change: string): string => change.replace(/[↑↓⟳~]$/, "");

/** Primitives mentioned by mode rows, including wildcard rows; cached per definition. */
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

/** Context available to facts and value functions; V is scalar or plane-valued. */
export interface ValueContext<V extends Value = number> {
  t: number;
  input: InputLayer;
  /** Resolved parameter table for this instance. */
  params: ParamTable;
  hostValue: V;
  /** Channel state and mapped hand state. Hand velocity uses samples since press; the release row retains the final hand state, otherwise absent hand control yields null. */
  channel: { x: V; v: V; hand: { x: V; v: V } | null; origin: V; segment: V | null };
  geometry: Geometry | null;
  def: Definition<V>;
}

export interface FactContext extends ValueContext<Value> {
  rect: { left: number; top: number; right: number; bottom: number } | null;
  /** Whether the last left press targeted this component; false before any press. */
  pressedOn: boolean;
  /** Mapped hand position before boundary clamping, or null without hand control. */
  handUnbounded: Value | null;
  /** Channel binding description used in traces. */
  bindingLabel: string;
}
