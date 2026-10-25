// 优化器（协议第 4.7 节）：给运行时的查询换上结果相同、更快的算法。不改构造，只改什么时候算、怎么算。
// 每一项写：查询、阶段、算法、相同的条件、对照怎么比。新的优化有了证据（采样）才加，写成同样的一项。

import type { Geometry } from "./definition.ts";
import { byValue, pick } from "./plan.ts";
import type { ExecMode, Mismatch, Query } from "./plan.ts";
import { fieldMotion, fieldMotionOnDemand } from "./physics.ts";
import type { FieldMotion, MotionArgs } from "./physics.ts";
import { vec } from "./vec.ts";
import type { Value } from "./vec.ts";

/** 两个值每个分量都完全相同（对照比的是同一串计算，不留容差）。 */
const exact = (a: Value, b: Value) => { const p = vec(a); const q = vec(b); return p.x === q.x && p.y === q.y; };
const text = (v: unknown) => JSON.stringify(v);

/**
 * 一段运动（阶段：一段）。原始：一段开始时整段推完。优化：按同一串 0.5 ms 的步推到要读的时刻为止，下次接着推；
 * 没有被带向的放置点时（手放开要落点）一开始就要停下的位置，照旧一次推完。每读一次（位置、停了没有、停下的结果）比一次。
 */
const MOTION: Query<MotionArgs, FieldMotion> = {
  name: "一段运动",
  same: "无条件（同一串 0.5 ms 的步）",
  reference: (...a) => fieldMotion(...a),
  optimized: (...a) => (a[6] === null ? fieldMotion(...a) : fieldMotionOnDemand(...a)),
  compare(ref, opt, differ) {
    const check = <T>(what: string, r: T, o: T, eq: (r: T, o: T) => boolean): T => {
      if (!eq(r, o)) differ(`${what}：原始 ${text(r)}；优化 ${text(o)}`);
      return o;
    };
    const same = (r: unknown, o: unknown) => r === o;
    return {
      place: opt.place,
      get durationMs() { return check("推进时长", ref.durationMs, opt.durationMs, same); },
      get rest() { return check("停下的位置", ref.rest, opt.rest, exact); },
      get landing() { return check("落点", ref.landing, opt.landing, exact); },
      get reached() { return check("到没到终态", ref.reached, opt.reached, same); },
      at: (ms) => check(`第 ${ms} ms 的位置、速度、加速度`, ref.at(ms), opt.at(ms), (r, o) => exact(r.x, o.x) && exact(r.v, o.v) && exact(r.a, o.a)),
      stopped: (ms) => check(`第 ${ms} ms 停下了没有`, ref.stopped(ms), opt.stopped(ms), same),
      lines: opt.lines,
      endLines: () => opt.endLines(),
    };
  },
};

/**
 * 几何（阶段：手势）。原始：每次向呈现层要。优化：一次手势里（进入独占模式到离开）用按下时读的那一份 pressed；不在手势里照旧要。
 * 每次都比两边。
 */
const GEOMETRY: Query<[ask: () => Geometry | null, pressed: Geometry | null], Geometry | null> = {
  name: "几何",
  same: "一次手势里部件不被移动（拖动中滚轮、滚页面本来就忽略）",
  reference: (ask) => ask(),
  optimized: (ask, pressed) => pressed ?? ask(),
  compare: byValue((r, o) => {
    const eq = r === o || (r !== null && o !== null && r.center === o.center && exact(r.atMin, o.atMin) && exact(r.atMax, o.atMax));
    return eq ? null : `原始 ${text(r)}；优化 ${text(o)}`;
  }),
};

/** 运行时用的查询：每个组件实例一份，对照读到的不同记在这个实例名下。 */
export interface Queries {
  motion(...a: MotionArgs): FieldMotion;
  geometry(ask: () => Geometry | null, pressed: Geometry | null): Geometry | null;
}

export function queries(mode: ExecMode, report: (m: Mismatch) => void): Queries {
  return { motion: pick(MOTION, mode, report), geometry: pick(GEOMETRY, mode, report) };
}
