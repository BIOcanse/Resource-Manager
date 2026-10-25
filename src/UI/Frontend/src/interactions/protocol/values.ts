// 可复用的取值（协议第 4.1 节第 4 项）：和可复用的场一样，常用的判定写一次，组件直接用，手感统一靠复用
// （用户「想要让手感统一就多复用算法」）。每个可复用的取值带上自己声明的参数：组件定义把 fn 放进 values，把 declares 放进 declares；
// 参数照常级联，组件要不同的值就在定义里写（例如分页的投影时长 300 ms）。

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
/** 投影时长的默认值：UIScrollView 正常减速率 d = 0.998 从速度 v 减到停走 v × d/(1 − d) ms 的距离（WWDC18 Designing Fluid Interfaces 的投影）。 */
export const IOS_PROJECTION_MS = 0.998 / (1 - 0.998);
export const PROJECTION_MS: ParamSpec = declared(PROJECTION_BY, "projectionMs", "投影时长", "ms", IOS_PROJECTION_MS,
  `${PROJECTION_BY}：投影 = 位置 + 速度 × 它；默认是 UIScrollView 减速率 0.998 的 d/(1 − d) ms`);

/**
 * 投影：位置 + 速度 × 投影时长，每条轴各算。手带着时（手放开的那一行读到放开那一刻的）从手算，否则从物体算：
 * 手绝对吸附时两者相同；手只是放置点时物体还落在手后面，用户要去的是手的投影（复刻 Embla 的分页、平滑滑块都读手）。
 */
export function projection<V extends Value>(ctx: ValueContext<V>): { p: V; explain: string } {
  const ms = ctx.params.projectionMs;
  const k = ms.value / 1000;
  const { x, v } = ctx.channel.hand ?? ctx.channel;
  const p = (typeof x === "number" ? x + (v as number) * k : { x: x.x + vec(v).x * k, y: x.y + vec(v).y * k }) as V;
  return { p, explain: `投影 = ${ctx.channel.hand ? "手" : "位置"} ${fmtValue(x)} + 速度 ${fmtValue(v)} × ${show(ms.value)} ms = ${fmtValue(p)}（${cite(ms)}）` };
}

/**
 * 指针处：指针此刻的屏幕位置按几何换成通道的值（不出边界前的值，可以在边界外）。没有几何时换不了，报错。
 * 用处：要交给宿主“指针在哪”、而物体自己不跟手的组件，例如拖动排序项（协议第 4.4 节第 3 处）：调用写 over(指针处)、commit(指针处)，
 * 宿主映射把它换成指针下是哪一项。几何取 1 个单位 = 1 个屏幕 px 时，值就是指针的屏幕坐标。
 */
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

/** 离 p 最近的点：直线距离，按给的次序找，距离相同取先找到的。 */
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

/** 通道的输出映射点里离 p 最近的那个（按 nearestOf），日志写名字和距离；names 不给就写第几个。没有点时报错。 */
export function nearestPoint<V extends Value>(ctx: ValueContext<V>, p: V, names?: readonly string[], who = "取值"): { value: V; explain: string } {
  const points = ctx.def.channel.mappingPoints;
  if (!points?.length) throw new Error(`${who}要通道有输出映射点`);
  const n = nearestOf(points, p);
  const label = names?.[n.index] ?? `第 ${n.index + 1} 个`;
  return { value: points[n.index], explain: `离映射点「${label}」${fmtValue(points[n.index])} 最近（${n.distance.toFixed(1)}；按映射点的次序找，距离相同取先找到的）` };
}

/**
 * 离投影最近的映射点：投影 = 位置 + 速度 × 投影时长（手带着时从手算），取通道的输出映射点里离它最近的（直线距离，按映射点的次序找，距离相同取先找到的）。
 * 速度 0 时投影就是当前位置，所以点一下不拖也能用；投影时长 0 就是离手（或位置）最近的映射点。names 是映射点的名字，日志用，不给就写第几个。
 * 出处：画中画（WWDC18 Pip.swift 的 project + nearestCorner）推广来的。
 */
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
