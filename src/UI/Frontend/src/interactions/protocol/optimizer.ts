// Use equivalent query algorithms that change when and how results are computed.


import type { Geometry } from "./definition.ts";
import { byValue, pick } from "./plan.ts";
import type { ExecMode, Mismatch, Query } from "./plan.ts";
import { fieldMotion, fieldMotionOnDemand } from "./physics.ts";
import type { FieldMotion, MotionArgs } from "./physics.ts";
import { vec } from "./vec.ts";
import type { Value } from "./vec.ts";

/** Compare every component exactly; both paths use the same arithmetic sequence. */
const exact = (a: Value, b: Value) => { const p = vec(a); const q = vec(b); return p.x === q.x && p.y === q.y; };
const text = (v: unknown) => JSON.stringify(v);

/** Advance targeted motion lazily through identical 0.5ms steps. Release motion still predicts its full landing immediately; comparison checks each read. */
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

/** Cache pressed geometry during pointer ownership; query current geometry outside a gesture and compare both paths when requested. */
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

/** Per-instance runtime queries attribute comparison failures to their instance. */
export interface Queries {
  motion(...a: MotionArgs): FieldMotion;
  geometry(ask: () => Geometry | null, pressed: Geometry | null): Geometry | null;
}

export function queries(mode: ExecMode, report: (m: Mismatch) => void): Queries {
  return { motion: pick(MOTION, mode, report), geometry: pick(GEOMETRY, mode, report) };
}
