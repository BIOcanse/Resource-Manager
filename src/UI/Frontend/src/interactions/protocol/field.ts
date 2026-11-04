// Each channel has one mathematical field that supplies acceleration, velocity, or position.

// Placement points are fixed output points, a moving hand target, or a terminal value.
// Hand and terminal targets are mutually exclusive; otherwise the field chooses among output points.
// Fields use per-second units and a fixed runtime step for frame-rate-independent results.
// Compute in pixel vectors: px, px/s, and px/s²; a line has a zero second component.
// Relations.label converts positions to channel values for traces.
// begin chooses segment behavior from initial relations.
// Each step reads current state and segment time; compute segment constants once in begin.

import { cubicBezier } from "./ease.ts";
import type { ParamId, ParamSpec, ParamTable } from "./params.ts";
import { cite, declared, show } from "./params.ts";
import { add, isZero, len, same, scale, sub, unit, ZERO } from "./vec.ts";
import type { Vec } from "./vec.ts";

/** Pixel position and velocity vectors. */
export interface State { x: Vec; v: Vec }

/** Hand or terminal target kind. */
export type Toward = "手" | "终态";

/** Immutable relations captured at segment start. */
export interface Relations {
  /** Output mapping positions in pixels. */
  points: readonly Vec[];
  /** Nearest output point by distance; ties prefer the later point; null for no points. */
  nearest(x: Vec): Vec | null;
  params: ParamTable;
  /** Format a pixel position as a channel value for traces. */
  label(px: Vec): string;
  /** Initial pixel position and velocity. */
  start: State;
  /** Hand or terminal target in pixels; null when predicting unconstrained release motion. */
  target: Vec | null;
  /** Target kind, or null without a target. */
  toward: Toward | null;
  /** Final mapped hand state for a release-started segment, otherwise null. Field-driven objects may trail the hand. */
  letGo: State | null;
  /** Continuous field-motion time before this segment, in seconds. Retargeting preserves it; stopping or direct hand control resets it. */
  moving: number;
}

/** Field output: acceleration, velocity, or end-of-step position. */
export type FieldOutput = { a: Vec } | { v: Vec } | { x: Vec };

/** Step state and seconds since the target last changed, measured at the step end. */
export type Step<O extends FieldOutput = FieldOutput> = (s: State, t: number) => O;

export interface Field<O extends FieldOutput = FieldOutput> {
  /** Name shown in traces and component views. */
  readonly name: string;
  /** Parameters read by the field. */
  readonly params: readonly ParamId[];
  /** Custom declarations merged into the component parameter table. */
  readonly declares?: readonly ParamSpec[];
  /** Choose segment behavior and return its per-step field function. */
  begin(r: Relations): Step<O>;
  /** Expression and parameter explanation for traces. */
  describe(params: ParamTable): string[];
}

const fmt = (n: number) => {
  const s = Math.abs(n) >= 100 ? n.toFixed(1) : n.toFixed(3).replace(/\.?0+$/, "");
  return s === "-0" ? "0" : s;
};

/** Zero acceleration everywhere. */
export const NO_FIELD: Field<{ a: Vec }> = {
  name: "无",
  params: [],
  begin: () => () => ({ a: ZERO }),
  describe: () => ["处处为 0"],
};

/** Zero velocity everywhere, stopping release motion in place. */
export const HOLD: Field<{ v: Vec }> = {
  name: "原地停",
  params: [],
  begin: () => () => ({ v: ZERO }),
  describe: () => ["处处给速度 0：松手就停在原处"],
};

/** Attract toward the target or nearest output point with exponentially decaying magnitude; zero at the point. */
export const PULL: Field<{ a: Vec }> = {
  name: "吸附",
  params: ["fieldStrength", "fieldLength"],
  begin(r) {
    const A = r.params.fieldStrength.value;
    const L = r.params.fieldLength.value;
    return (s) => {
      const p = r.target ?? r.nearest(s.x);
      if (p === null) return { a: ZERO };
      const d = sub(p, s.x);
      const dist = len(d);
      if (dist === 0) return { a: ZERO };
      return { a: scale(unit(d), A * Math.exp(-dist / L)) };
    };
  },
  describe(p) {
    const A = p.fieldStrength.value;
    const L = p.fieldLength.value;
    const range = A > p.staticFriction.value ? L * Math.log(A / p.staticFriction.value) : 0;
    return [`吸附：指向被带向的放置点，没有时指向最近的输出映射点，大小 ${show(A)}·e^(−距离/${show(L)}) px/s²（${cite(p.fieldStrength)}；${cite(p.fieldLength)}）；`
      + `加上摩擦时，静止松手的吸附范围 = L·ln(A/静摩擦) = ${show(Number(range.toFixed(2)))} px（推论）`];
  },
};

/** Underdamping reaches the point in finite time, where point absorption stops motion. A 0.7 ratio avoids a slow, numerically sensitive approach. */
const ZETA = 0.7;
/** Angular frequency for first arrival from rest after ms milliseconds. */
export const springOmega = (ms: number) => (Math.PI - Math.acos(ZETA)) / ((ms / 1000) * Math.sqrt(1 - ZETA * ZETA));

/** Underdamped return spring toward the target or nearest output point. */
export const SPRING: Field<{ a: Vec }> = {
  name: "回点弹簧",
  params: ["springTime"],
  begin(r) {
    const w = springOmega(r.params.springTime.value);
    return (s) => {
      const p = r.target ?? r.nearest(s.x);
      if (p === null) return { a: ZERO };
      return { a: sub(scale(sub(p, s.x), w * w), scale(s.v, 2 * ZETA * w)) };
    };
  },
  describe(p) {
    const w = springOmega(p.springTime.value);
    return [`回点弹簧：指向被带向的放置点，没有时指向最近的输出映射点，ω²·位移 − 2·${ZETA}·ω·速度，ω = ${fmt(w)} /s（${cite(p.springTime)}）；`
      + `从静止出发正好 ${show(p.springTime.value)} ms 到点，和距离无关；到点时速度 = 距离 × ${fmt(w * Math.exp(-ZETA * w * p.springTime.value / 1000))} /s`];
  },
};

/** Add kinetic/static friction to an acceleration field. The stationary attraction boundary occurs where force equals static friction. */
export function withFriction(g: Field<{ a: Vec }>): Field<{ a: Vec }> {
  return {
    name: `${g.name} + 摩擦`,
    params: [...g.params, "kineticFriction", "staticFriction"],
    begin(r) {
      const inner = g.begin(r);
      const mk = r.params.kineticFriction.value;
      const ms = r.params.staticFriction.value;
      return (s, t) => {
        const G = inner(s, t).a;
        if (!isZero(s.v)) return { a: sub(G, scale(unit(s.v), mk)) };
        return { a: len(G) <= ms ? ZERO : sub(G, scale(unit(G), mk)) };
      };
    },
    describe(p) {
      return [...g.describe(p), `摩擦：运动时减去和速度同向的 ${show(p.kineticFriction.value)} px/s²（${cite(p.kineticFriction)}）；`
        + `静止时其余部分不超过 ${show(p.staticFriction.value)} px/s² 就是 0（${cite(p.staticFriction)}）；只有摩擦时滑行距离 = 速度² ÷ (2·动摩擦)`];
    },
  };
}

/** Scalar scrolling decays exponentially inside bounds and uses a critically damped return outside. Stop-distance thresholds terminate asymptotic motion; the consumer supplies prefixed parameter defaults. */
export function scrollField(by: string, prefix: string, min: number, max: number, decelRate: number, stopPx: number, bouncePeriodMs: number): Field {
  const id = { rate: `${prefix}DecelRate`, stop: `${prefix}StopDistance`, period: `${prefix}BouncePeriod` };
  const name = `滚动（${by}）`;
  const who = `力场「${name}」`;
  const K = (p: ParamTable) => -Math.log(p[id.rate].value) * 1000;
  const w = (p: ParamTable) => (2 * Math.PI) / (p[id.period].value / 1000);
  return {
    name,
    params: Object.values(id),
    declares: [
      declared(who, id.rate, `${by}滚动减速率`, "/ms", decelRate, `${who}：${min}–${max} 里每 ms 速度乘它`),
      declared(who, id.stop, `${by}滚动停下距离`, "px", stopPx, `${who}：剩余距离或离边缘不到它时直接给速度 0`),
      declared(who, id.period, `${by}回弹周期`, "ms", bouncePeriodMs, `${who}：越过边缘后回到边缘的弹簧，ω = 2π ÷ 它`),
    ],
    begin(r) {
      const stop = r.params[id.stop].value;
      const k = K(r.params);
      const om = w(r.params);
      return (s) => {
        const x = s.x.x;
        const v = s.v.x;
        const e = x < min ? min : x > max ? max : null;
        if (e === null) return Math.abs(v) / k < stop ? { v: ZERO } : { a: { x: -k * v, y: 0 } };
        const d = e - x;
        if (Math.abs(d) < stop && Math.abs(v) < stop * om) return { v: ZERO };
        return { a: { x: om * om * d - 2 * om * v, y: 0 } };
      };
    },
    describe: (p) => {
      const k = K(p);
      const stop = p[id.stop].value;
      return [
        `${min}–${max} px 里 a = −${k.toFixed(3)}·速度（${cite(p[id.rate])}）；剩余距离 速度 ÷ ${k.toFixed(3)} 不到 ${show(stop)} px 时直接给速度 0（${cite(p[id.stop])}）`,
        `越过边缘：回到边缘的临界阻尼弹簧 a = ω²·位移 − 2ω·速度，ω = ${w(p).toFixed(2)} /s（${cite(p[id.period])}）；离边缘不到 ${show(stop)} px、速度不到 ${(stop * w(p)).toFixed(2)} px/s 时直接给速度 0`,
      ];
    },
  };
}

/** Choose one field at segment start from immutable relations; merge both parameter sets and declarations. */
export function byCase(name: string, when: string, test: (r: Relations) => boolean, yes: Field, otherwise: string, no: Field): Field {
  return {
    name,
    params: [...new Set([...yes.params, ...no.params])],
    declares: [...(yes.declares ?? []), ...(no.declares ?? [])],
    begin: (r) => (test(r) ? yes.begin(r) : no.begin(r)),
    describe: (p) => [...yes.describe(p).map((d) => `${when}：${d}`), ...no.describe(p).map((d) => `${otherwise}：${d}`)],
  };
}

/** Use terminal while targeting a terminal value, otherwise other. */
export const toTerminal = (other: Field, terminal: Field): Field =>
  byCase(`${other.name}；带向终态时「${terminal.name}」`, "平时", (r) => r.toward !== "终态", other, "带向终态时", terminal);

// Time-based position fields stop in place without a target.

/** Cubic motion preserves initial position/velocity and reaches the target at zero velocity after the configured duration. */
export const CUBIC: Field<{ x: Vec }> = {
  name: "三次曲线",
  params: ["settleTime"],
  begin(r) {
    const from = r.start;
    if (r.target === null) return () => ({ x: from.x });
    const to = r.target;
    const T = r.params.settleTime.value / 1000;
    return (_s, t) => {
      if (!(T > 0) || t >= T) return { x: to };
      const u = t / T;
      const u2 = u * u;
      const u3 = u2 * u;
      const h = (x0: number, v0: number, x1: number) => (2 * u3 - 3 * u2 + 1) * x0 + (u3 - 2 * u2 + u) * T * v0 + (-2 * u3 + 3 * u2) * x1;
      return { x: { x: h(from.x.x, from.v.x, to.x), y: h(from.x.y, from.v.y, to.y) } };
    };
  },
  describe: (p) => [`三次曲线：从这一段开始时的位置和速度出发，正好 ${show(p.settleTime.value)} ms 到被带向的放置点、速度为 0（${cite(p.settleTime)}）；没有时原地停`],
};

/** CSS cubic-bezier interpolation from initial position to target, ignoring initial velocity. Prefixed parameters supply duration and curve. */
export function easeField(by: string, prefix: string, ms: number, curve: readonly [number, number, number, number]): Field<{ x: Vec }> {
  const id = { ms: `${prefix}EaseMs`, x1: `${prefix}EaseX1`, y1: `${prefix}EaseY1`, x2: `${prefix}EaseX2`, y2: `${prefix}EaseY2` };
  const name = `缓动（${by}）`;
  const who = `场「${name}」`;
  return {
    name,
    params: Object.values(id),
    declares: [
      declared(who, id.ms, `${by}缓动时长`, "ms", ms, `${who}：从开始到终态的用时`),
      declared(who, id.x1, `${by}缓动曲线 x1`, "", curve[0], `${who}：cubic-bezier 的第一个控制点 x`),
      declared(who, id.y1, `${by}缓动曲线 y1`, "", curve[1], `${who}：cubic-bezier 的第一个控制点 y`),
      declared(who, id.x2, `${by}缓动曲线 x2`, "", curve[2], `${who}：cubic-bezier 的第二个控制点 x`),
      declared(who, id.y2, `${by}缓动曲线 y2`, "", curve[3], `${who}：cubic-bezier 的第二个控制点 y`),
    ],
    begin(r) {
      const from = r.start.x;
      if (r.target === null) return () => ({ x: from });
      const to = r.target;
      const p = r.params;
      const T = p[id.ms].value / 1000;
      const ease = cubicBezier(p[id.x1].value, p[id.y1].value, p[id.x2].value, p[id.y2].value);
      return (_s, t) => {
        if (!(T > 0) || t >= T) return { x: to };
        return { x: add(from, scale(sub(to, from), ease(t / T))) };
      };
    },
    describe: (p) => [`缓动：开始时的位置 + (终态 − 开始时的位置) × cubic-bezier(${[id.x1, id.y1, id.x2, id.y2].map((k) => show(p[k].value)).join(", ")})(时间 ÷ ${show(p[id.ms].value)} ms)，终态是被带向的放置点，不接开始时的速度（${cite(p[id.ms])}）；没有时原地停`],
  };
}

/** Analytic scalar spring solution for underdamped, critically damped, and overdamped motion. */
export function springAt(x0: number, v0: number, e: number, w: number, z: number, t: number): { x: number; v: number } {
  const A = x0 - e;
  if (z === 1) {
    const B = v0 + w * A;
    const ex = Math.exp(-w * t);
    return { x: e + (A + B * t) * ex, v: (B - w * (A + B * t)) * ex };
  }
  if (z < 1) {
    const wd = w * Math.sqrt(1 - z * z);
    const C = (v0 + z * w * A) / wd;
    const ex = Math.exp(-z * w * t);
    const c = Math.cos(wd * t);
    const s = Math.sin(wd * t);
    return { x: e + ex * (A * c + C * s), v: ex * ((C * wd - z * w * A) * c - (A * wd + z * w * C) * s) };
  }
  const q = w * Math.sqrt(z * z - 1);
  const r1 = -z * w + q;
  const r2 = -z * w - q;
  const c2 = (v0 - r1 * A) / (r2 - r1);
  const c1 = A - c2;
  return { x: e + c1 * Math.exp(r1 * t) + c2 * Math.exp(r2 * t), v: r1 * c1 * Math.exp(r1 * t) + r2 * c2 * Math.exp(r2 * t) };
}

/** Analytic spring from initial state to target; snap within position and velocity thresholds to terminate asymptotic motion. Without a target, stop in place. */
export function springField(by: string, prefix: string, responseMs: number, damping: number, stopPx: number): Field<{ x: Vec }> {
  const id = { response: `${prefix}SpringResponse`, damping: `${prefix}SpringDamping`, stop: `${prefix}SpringStop` };
  const name = `弹簧（${by}）`;
  const who = `场「${name}」`;
  const w = (p: ParamTable) => (2 * Math.PI) / (p[id.response].value / 1000);
  return {
    name,
    params: Object.values(id),
    declares: [
      declared(who, id.response, `${by}弹簧响应时间`, "ms", responseMs, `${who}：ω = 2π ÷ 它`),
      declared(who, id.damping, `${by}弹簧阻尼比`, "", damping, `${who}：1 是临界阻尼，从静止出发不过冲；小于 1 会来回，大于 1 更慢`),
      declared(who, id.stop, `${by}弹簧停下距离`, "px", stopPx, `${who}：离终态不到它、速度不到它·ω 时直接到终态`),
    ],
    begin(r) {
      const from = r.start;
      if (r.target === null) return () => ({ x: from.x });
      const to = r.target;
      const om = w(r.params);
      const z = r.params[id.damping].value;
      const stop = r.params[id.stop].value;
      return (s, t) => {
        if (same(s.x, to)) return { x: to };
        const ax = springAt(from.x.x, from.v.x, to.x, om, z, t);
        const ay = springAt(from.x.y, from.v.y, to.y, om, z, t);
        const near = Math.hypot(ax.x - to.x, ay.x - to.y) < stop && Math.hypot(ax.v, ay.v) < stop * om;
        return { x: near ? to : { x: ax.x, y: ay.x } };
      };
    },
    describe: (p) => {
      const om = w(p);
      const stop = p[id.stop].value;
      return [`弹簧：每条轴 位置'' = −ω²·(位置 − 终态) − 2·${show(p[id.damping].value)}·ω·位置'，ω = ${fmt(om)} /s（${cite(p[id.response])}；${cite(p[id.damping])}），从开始时的位置和速度出发，给解析解的位置；`
        + `终态是被带向的放置点，离它不到 ${show(stop)} px、速度不到 ${fmt(stop * om)} px/s 时到它（${cite(p[id.stop])}）；没有时原地停`];
    },
  };
}

/** Velocity multiplier after absorbing fraction r of kinetic energy. */
export const keep = (r: number) => Math.sqrt(Math.max(0, 1 - r));
