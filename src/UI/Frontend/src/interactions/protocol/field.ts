// 力场（协议第 4.5 节）：和现实没有关系，就是数学逻辑加放置点。读什么、给什么都不限制：
// 这一刻给物体的加速度，或者直接给速度，或者直接给位置。每个通道一个力场。
// 放置点：输出映射点（组件定的，固定）；手（手只是放置点时，手带着时在手的位置，跟着手动）；终态（宿主值和物体将要停的值不同时，或停住点给了一个值）。
// 手和终态至多有一个，就是物体被带向的放置点（Relations.target）；都没有时场自己看输出映射点（例如吸附指向最近的点）。
// 常用的写法做成可复用的场，组件直接用或组合，手感统一靠复用。都按“每秒多少”写，推进的步长由运行时固定，结果不随帧率变化。
// 场在 px 平面上算（协议第 4.5 节“平面”）：位置、速度、给的量都是向量，单位 px、px/s、px/s²；线是第二个分量恒为 0 的平面。
// 对外写日志时由 Relations.label 换成值。
// 写法分两部分（协议第 4.7 节“阶段”）：begin 在一段开始时按这一段开始时的量（Relations）定下这一段的走法，返回每一步给的量；
// 每一步只按这一刻的位置、速度和这一段开始以来的时间算。一段里不变的东西在 begin 里算一次，场自己不缓存。

import { cubicBezier } from "./ease.ts";
import type { ParamId, ParamSpec, ParamTable } from "./params.ts";
import { cite, declared, show } from "./params.ts";
import { add, isZero, len, same, scale, sub, unit, ZERO } from "./vec.ts";
import type { Vec } from "./vec.ts";

/** 物体：位置 x（px）、速度 v（px/s），都是平面上的向量。 */
export interface State { x: Vec; v: Vec }

/** 物体被带向的放置点是哪一种：手（跟着手动），或终态。 */
export type Toward = "手" | "终态";

/** 场在一段开始时能读的东西，一段里都不变。要读别的就加在这里。 */
export interface Relations {
  /** 输出映射点的位置（px）。 */
  points: readonly Vec[];
  /** 离 x 最近的输出映射点（px，按直线距离），距离相同取排在后面的（线上就是较大的）；没有点时为 null。 */
  nearest(x: Vec): Vec | null;
  params: ParamTable;
  /** 把位置（px）写成值，日志用。 */
  label(px: Vec): string;
  /** 这一段开始时的位置和速度（px、px/s）。 */
  start: State;
  /** 物体被带向的放置点（px）：手或终态；都没有时为 null（手放开后按力场走、推出落点）。吸附、回点弹簧有它时指向它。 */
  target: Vec | null;
  /** target 是哪一种；没有时为 null。 */
  toward: Toward | null;
  /**
   * 这一段是手放开开始的时候，放开那一刻的手（px、px/s）：手带着物体去的位置（经过手的映射）和它的速度；别的时候为 null。
   * 手绝对吸附时就是物体这一刻的位置和速度；手只是放置点时两者不同（物体落在手后面）。
   * 例：复刻 Embla 的分页，松手的滚动体按手的速度算出的力度、和手的位置到终态的距离定时长和摩擦；复刻 Motion 的拖动排序，手放开回位用弹簧，被换位让开用缓动。
   */
  letGo: State | null;
  /**
   * 物体这次被力场连续带着动了多久（s，到这一段开始时）：从静止开始的这一段是 0；放置点变了、物体还在动就接着算，停下或被按住就结束。
   * 例：复刻 Embla 的分页，滚动体按固定步长连续推进，拖动中手动了、松手换了时长和摩擦都接着同一串步走，场按它找到步的位置。
   */
  moving: number;
}

/** 场这一刻给的：加速度 a（px/s²），或者直接给速度 v（px/s），或者直接给这一步结束时的位置 x（px）。 */
export type FieldOutput = { a: Vec } | { v: Vec } | { x: Vec };

/**
 * 一段里的每一步：这一刻的位置、速度（px、px/s），和这一段开始以来的时长 t（s，到这一步结束时）。
 * 一段：放置点上一次变了（手动了、手放开了、终态出现或变了）以来。
 */
export type Step<O extends FieldOutput = FieldOutput> = (s: State, t: number) => O;

export interface Field<O extends FieldOutput = FieldOutput> {
  /** 名字：日志和组件卡片用。 */
  readonly name: string;
  /** 读哪些参数（参数面板据此标出本组件不读的项）。 */
  readonly params: readonly ParamId[];
  /** 自己声明的参数（协议第 5 节“场和取值自己的参数”），并进组件的值表。 */
  readonly declares?: readonly ParamSpec[];
  /** 一段开始：按这一段开始时的量定下这一段的走法，返回每一步给的加速度、速度或位置。 */
  begin(r: Relations): Step<O>;
  /** 表达式和参数、性质的直接关系，日志照写。 */
  describe(params: ParamTable): string[];
}

const fmt = (n: number) => {
  const s = Math.abs(n) >= 100 ? n.toFixed(1) : n.toFixed(3).replace(/\.?0+$/, "");
  return s === "-0" ? "0" : s;
};

/** 没有场：处处为 0。 */
export const NO_FIELD: Field<{ a: Vec }> = {
  name: "无",
  params: [],
  begin: () => () => ({ a: ZERO }),
  describe: () => ["处处为 0"],
};

/** 原地停：处处给速度 0，松手就停在原处。 */
export const HOLD: Field<{ v: Vec }> = {
  name: "原地停",
  params: [],
  begin: () => () => ({ v: ZERO }),
  describe: () => ["处处给速度 0：松手就停在原处"],
};

/** 吸附：指向物体被带向的放置点，没有时指向最近的输出映射点，大小 A·e^(−距离/L)；在点上为 0，到两点距离相同处方向翻转。 */
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

/**
 * 回点弹簧的阻尼比：小于 1，所以在有限时间内到点（到点时由点吸能停下），而且时间和距离无关。
 * 取 0.7：越靠近 1，最后一段越慢（0.9 时到点速度只有 距离 × 0.13 /s，离点 0.5 px 后还要蠕动约 60 ms，画面上早就“到了”），
 * 到点时间对推进误差也越敏感（0.9 时按 0.5 ms 一步算出 186 ms）。0.7 时到点速度 = 距离 × 1.83 /s，算出的到点时间和理论差不到 0.1 ms。
 */
const ZETA = 0.7;
/** 从静止出发正好 ms 毫秒到点的 ω（1/s）：第一次到点的时刻 = (π − arccos ζ) ÷ (ω·√(1 − ζ²))。 */
export const springOmega = (ms: number) => (Math.PI - Math.acos(ZETA)) / ((ms / 1000) * Math.sqrt(1 - ZETA * ZETA));

/** 回点弹簧：指向物体被带向的放置点，没有时指向最近的输出映射点，ω²·位移 − 2·0.7·ω·速度。 */
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

/**
 * 在给加速度的场 g 上加摩擦：运动时减去和速度同向的动摩擦；静止时 g 不超过静摩擦就是 0，否则减去和 g 同向的动摩擦。
 * 静止松手的吸附范围由此推出：g 等于静摩擦的地方。
 */
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

/**
 * 滚动（复刻 iOS 列表时加的）：[min, max] 里按减速率衰减，a = −K·速度（K = −1000·ln 减速率，减速率是每 ms 速度乘多少），
 * 剩余距离（速度 ÷ K）不到停下距离时直接给速度 0；越过边缘后是回到边缘的临界阻尼弹簧（ω = 2π ÷ 回弹周期），
 * 离边缘不到停下距离、速度不到 停下距离·ω 时直接给速度 0。减速永远到不了 0，弹簧永远到不了边缘：停下靠场直接给速度 0。
 * 减速率、停下距离、回弹周期是它声明的参数，名字带上前缀，默认值由用它的组件给。只用于一条轴的通道（第二个分量恒为 0）。
 */
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

/**
 * 按情况选场：test 成立时按 yes，否则按 no；test 只读一段开始时的量，所以一段开始时就选定。参数、声明两边合起来，日志写两边各在什么情况用。
 * 一个通道一个力场、场读什么不限制，要按情况分两种运动时就这样把两个场组合成一个。例：带向终态时换一个场（下面的 toTerminal）；
 * 复刻的 Sonner 按去向选回去或关闭的缓动；复刻的拖动排序按这一段是不是手放开开始的选弹簧或缓动。
 */
export function byCase(name: string, when: string, test: (r: Relations) => boolean, yes: Field, otherwise: string, no: Field): Field {
  return {
    name,
    params: [...new Set([...yes.params, ...no.params])],
    declares: [...(yes.declares ?? []), ...(no.declares ?? [])],
    begin: (r) => (test(r) ? yes.begin(r) : no.begin(r)),
    describe: (p) => [...yes.describe(p).map((d) => `${when}：${d}`), ...no.describe(p).map((d) => `${otherwise}：${d}`)],
  };
}

/** 按放置点分开写：物体被带向终态时按 terminal，平时按 other。例：纵向点平时是吸附加摩擦，宿主值变了用三次曲线到终态。 */
export const toTerminal = (other: Field, terminal: Field): Field =>
  byCase(`${other.name}；带向终态时「${terminal.name}」`, "平时", (r) => r.toward !== "终态", other, "带向终态时", terminal);

// ===== 带向放置点、按这一段开始以来的时间给位置的场；没有被带向的放置点时原地停（给的位置就是这一段开始时的位置） =====

/** 三次曲线：从这一段开始时的位置和速度出发，正好“到位时间”到被带向的放置点、速度为 0。每个分量各算各的。 */
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

/**
 * 缓动：开始时的位置 + (终态 − 开始时的位置) × 缓动(时间 ÷ 时长)，缓动是 CSS 的 cubic-bezier，不接开始时的速度。平面上走直线。
 * 时长和曲线的四个数是这个场声明的参数，名字带上前缀（例如 vaul），默认值由用它的组件给。
 */
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

/**
 * 弹簧从 x0、v0 出发 t 秒后的位置和速度（一条轴，终点 e）：x'' = −ω²·(x − e) − 2ζω·x' 的解。
 * 阻尼比 1 是临界阻尼，x = e + (A + B·t)·e^(−ωt)，A = x0 − e，B = v0 + ω·A；小于 1 是欠阻尼（会过冲、来回），大于 1 是过阻尼。
 */
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

/**
 * 弹簧：每条轴是从这一段开始时的位置和速度出发、带向被带向的放置点（终态）的弹簧 x'' = −ω²·(x − 终态) − 2ζω·x'，给这一刻的位置（解析解，和三次曲线、缓动一样读这一段开始以来的时间）。没有时原地停。
 * ω = 2π ÷ 响应时间、ζ 是阻尼比（UIKit、SwiftUI 弹簧的 response、dampingRatio），接开始时的速度，冲过终态会回来。
 * 弹簧永远到不了终态：离终态不到停下距离、速度不到 停下距离·ω 时给终态的位置；已经在终态上就一直给终态。
 * 响应时间、阻尼比、停下距离是这个场声明的参数，名字带上前缀，默认值由用它的组件给。
 */
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

/** 吸掉比例 r 的动能后剩下的速度倍数。 */
export const keep = (r: number) => Math.sqrt(Math.max(0, 1 - r));
