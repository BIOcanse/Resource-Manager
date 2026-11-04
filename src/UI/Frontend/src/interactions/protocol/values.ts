// Reusable value functions carry their own parameter declarations.
// Register fn in values and its parameter declarations in declares.
// The consuming definition can override cascaded defaults.

import type { ValueContext, ValueFn } from "./definition.ts";
import { frameOf } from "./physics.ts";
import type { ParamSpec } from "./params.ts";
import { cite, declared, show } from "./params.ts";
import { fmtValue, len, sub, vec } from "./vec.ts";
import type { Value } from "./vec.ts";

export interface ReusableValue<V extends Value> {
  readonly name: string;
  readonly fn: ValueFn<V>;
  readonly declares: readonly ParamSpec[];
}

const PROJECTION_BY = "取值「离投影最近的映射点」";
/** UIScrollView-style projection horizon d/(1-d) milliseconds for deceleration rate d=0.998. */
export const IOS_PROJECTION_MS = 0.998 / (1 - 0.998);
export const PROJECTION_MS: ParamSpec = declared(PROJECTION_BY, "projectionMs", "投影时长", "ms", IOS_PROJECTION_MS,
  `${PROJECTION_BY}：投影 = 位置 + 速度 × 它；默认是 UIScrollView 减速率 0.998 的 d/(1 − d) ms`);

/** Project position by velocity times horizon on each axis. Use hand state while held, including the release row, because field-driven objects may trail the hand. */
export function projection<V extends Value>(ctx: ValueContext<V>): { p: V; explain: string } {
  const ms = ctx.params.projectionMs;
  const k = ms.value / 1000;
  const { x, v } = ctx.channel.hand ?? ctx.channel;
  const p = (typeof x === "number" ? x + (v as number) * k : { x: x.x + vec(v).x * k, y: x.y + vec(v).y * k }) as V;
  return { p, explain: `投影 = ${ctx.channel.hand ? "手" : "位置"} ${fmtValue(x)} + 速度 ${fmtValue(v)} × ${show(ms.value)} ms = ${fmtValue(p)}（${cite(ms)}）` };
}

/** Convert viewport pointer coordinates through channel geometry without clamping. Geometry is required; pixel-aligned geometry preserves screen coordinates. */
export const POINTER_AT: ReusableValue<Value> = {
  name: "指针处",
  declares: [],
  fn: (ctx) => {
    if (!ctx.geometry) throw new Error("取值「指针处」要组件有几何");
    const p = ctx.input.position();
    const value = frameOf(ctx.def.channel, ctx.geometry).valueOf(p);
    return { value, explain: `指针在 (${p.x.toFixed(1)}, ${p.y.toFixed(1)}) px，按几何换成值 ${fmtValue(value)}` };
  },
};

/** Nearest point by Euclidean distance; ties keep the first point. */
export function nearestOf<V extends Value>(points: readonly V[], p: V): { index: number; distance: number } {
  let index = 0;
  let distance = Infinity;
  for (let i = 0; i < points.length; i++) {
    const d = len(sub(vec(p), vec(points[i])));
    if (d < distance) {
      index = i;
      distance = d;
    }
  }
  return { index, distance };
}

/** Nearest output mapping point with trace name and distance; reject an empty point list. */
export function nearestPoint<V extends Value>(ctx: ValueContext<V>, p: V, names?: readonly string[], who = "取值"): { value: V; explain: string } {
  const points = ctx.def.channel.mappingPoints;
  if (!points?.length) throw new Error(`${who}要通道有输出映射点`);
  const n = nearestOf(points, p);
  const label = names?.[n.index] ?? `第 ${n.index + 1} 个`;
  return { value: points[n.index], explain: `离映射点「${label}」${fmtValue(points[n.index])} 最近（${n.distance.toFixed(1)}；按映射点的次序找，距离相同取先找到的）` };
}

/** Nearest mapping point to projected hand/object position; ties keep mapping order. Zero horizon selects by current position. */
export function nearestToProjection<V extends Value>(names?: readonly string[]): ReusableValue<V> {
  const name = "离投影最近的映射点";
  return {
    name,
    declares: [PROJECTION_MS],
    fn: (ctx) => {
      const pr = projection(ctx);
      const n = nearestPoint(ctx, pr.p, names, `取值「${name}」`);
      return { value: n.value, explain: `${pr.explain}；${n.explain}` };
    },
  };
}
