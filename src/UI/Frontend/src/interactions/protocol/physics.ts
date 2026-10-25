// 运动（协议第 4.5 节）：物体有位置、速度，由通道的一个力场带着。放置点是输出映射点、手、终态。
//   手：默认绝对吸附，按住时位置 = 手的映射（手的位置），不出边界，速度 = 位置的速度；通道写了“手只是放置点”时由力场带向手；
//   其余时候按力场推进：带向终态（宿主值和物体将要停的值不同时），或者没有被带向的放置点、推出落点。
//   场给加速度、速度或位置，每 0.5 ms 一步，结果不随帧率变化。放置点变了就从这一刻的位置和速度起一段：
//   原始执行（fieldMotion）一段开始时整段推完；优化后（fieldMotionOnDemand）按同一串步推到要读的时刻（协议第 4.7 节），结果相同。
// 特例点上的绝对规则：手放开时吸掉“松手吸能”比例的动能；给加速度、速度时，到达场的放置点、边界时吸掉“点吸能”“边界吸能”比例的动能；
// 给位置时只受边界限制。有几何时位置就是部件逻辑中心的位置。
// 平面（协议第 4.5 节“平面”）：推进在 px 平面上，每条轴 px = 值 × 这条轴每单位的 px；线是第二个分量恒为 0 的平面，
// 所以线和平面是同一套推进。对外（参数、结果）用通道的值：线是数，平面是点。

import { keep } from "./field.ts";
import type { Field, FieldOutput, Relations, State, Toward } from "./field.ts";
import type { ChannelDef, Geometry, Point } from "./definition.ts";
import type { InputLayer } from "./input.ts";
import type { ParamTable } from "./params.ts";
import { cite, show } from "./params.ts";
import { add, clampVec, dot, fmt, fmtValue, isZero, len, same, sameValue, scale, sub, unit, valueOfVec, vec, ZERO } from "./vec.ts";
import type { Value, Vec } from "./vec.ts";

/** 日志里的时长（ms）：推进按 0.5 ms 一步，最多 1 位小数，不补 0。 */
export const ms = (n: number) => String(Number(n.toFixed(1)));

/** 通道是平面（两条轴）：最小值是点。 */
export const isPlane = (ch: ChannelDef<Value>): boolean => typeof ch.min !== "number";

/** 值：位置按步长取整（平面上每条轴各自的步长）。只用于调用参数，画面不取整。 */
export function stepValue<V extends Value>(ch: ChannelDef<V>, x: V): V {
  if (!ch.step) return x;
  const st = vec(ch.step);
  const p = vec(x);
  const r = (v: number, s: number) => (s ? Math.round(v / s) * s : v);
  return valueOfVec(isPlane(ch), { x: r(p.x, st.x), y: r(p.y, st.y) }) as V;
}

/**
 * 几何换算：由呈现层报告的两点（值最小、最大时逻辑中心的位置）按比例得出（协议第 4.1 节第 6 项）。
 * 线：两点连线是运动方向。平面：两点是矩形的两个对角，x 轴对屏幕横向、y 轴对屏幕纵向。
 */
export interface Frame {
  plane: boolean;
  /** 每条轴值每变化 1 个单位，逻辑中心在屏幕上移动多少 px（线的第二条轴不用，记 1）。 */
  ppu: Vec;
  /** 每条轴值增大时逻辑中心在屏幕上移动的方向（单位向量）。线的第二条轴取和第一条垂直的方向，不用。 */
  dirX: Point;
  dirY: Point;
  /** 值为 v 时逻辑中心的屏幕位置（v 可以在边界外）。 */
  at(v: Value): Point;
  /** 屏幕上一点对应的值，不受边界限制（线上取沿运动方向的分量）。 */
  valueOf(p: Point): Value;
  /** 值单位的向量（速度、加速度）在屏幕上是多大的 px 向量。 */
  screen(v: Value): Point;
}

export function frameOf(ch: ChannelDef<Value>, g: Geometry): Frame {
  const lo = vec(ch.min);
  const hi = vec(ch.max);
  const dx = g.atMax.x - g.atMin.x;
  const dy = g.atMax.y - g.atMin.y;
  if (!isPlane(ch)) {
    const length = Math.hypot(dx, dy);
    const ppu = length / (hi.x - lo.x);
    const dir = length > 0 ? { x: dx / length, y: dy / length } : { x: 1, y: 0 };
    return {
      plane: false,
      ppu: { x: ppu, y: 1 },
      dirX: dir,
      dirY: { x: -dir.y, y: dir.x },
      at: (v) => { const u = vec(v).x; return { x: g.atMin.x + dir.x * (u - lo.x) * ppu, y: g.atMin.y + dir.y * (u - lo.x) * ppu }; },
      valueOf: (p) => lo.x + ((p.x - g.atMin.x) * dir.x + (p.y - g.atMin.y) * dir.y) / ppu,
      screen: (v) => { const u = vec(v).x * ppu; return { x: dir.x * u, y: dir.y * u }; },
    };
  }
  const ppu = { x: Math.abs(dx) / (hi.x - lo.x), y: Math.abs(dy) / (hi.y - lo.y) };
  const sx = dx < 0 ? -1 : 1;
  const sy = dy < 0 ? -1 : 1;
  return {
    plane: true,
    ppu,
    dirX: { x: sx, y: 0 },
    dirY: { x: 0, y: sy },
    at: (v) => { const u = vec(v); return { x: g.atMin.x + sx * (u.x - lo.x) * ppu.x, y: g.atMin.y + sy * (u.y - lo.y) * ppu.y }; },
    valueOf: (p) => ({ x: lo.x + ((p.x - g.atMin.x) * sx) / ppu.x, y: lo.y + ((p.y - g.atMin.y) * sy) / ppu.y }),
    screen: (v) => { const u = vec(v); return { x: sx * u.x * ppu.x, y: sy * u.y * ppu.y }; },
  };
}

/** 值 → px 平面：每条轴乘它的每单位 px。 */
const toPx = (ppu: Vec, v: Value): Vec => { const u = vec(v); return { x: u.x * ppu.x, y: u.y * ppu.y }; };
/** px 平面 → 值（正好在特殊的值上时见 valueAt）。 */
const fromPx = (plane: boolean, ppu: Vec, p: Vec): Value => valueOfVec(plane, { x: p.x / ppu.x, y: plane ? p.y / ppu.y : 0 });

/**
 * 手的位置：按下时手所在处 + 指针的位移在运动方向上的分量，换算成值（平面上每条轴各算各的）。
 * 按在部件上时，按下时手所在处就是逻辑中心，所以抓在部件哪里都一样，部件不会跳到指针下面；
 * 写了“跳到指针处”时是指针处（协议第 4.1 节第 6 项）。不受边界限制。
 */
export function handPosition<V extends Value>(ch: ChannelDef<V>, handStart: V, input: InputLayer, frame: Frame): { hand: V; explain: string } {
  const press = input.lastPress("pointer.left");
  const pos = input.position();
  const d = press ? { x: pos.x - press.x, y: pos.y - press.y } : ZERO;
  const moved = { x: dot(d, frame.dirX), y: frame.plane ? dot(d, frame.dirY) : 0 };
  const delta = { x: moved.x / frame.ppu.x, y: frame.plane ? moved.y / frame.ppu.y : 0 };
  const hand = valueOfVec(frame.plane, add(vec(handStart), delta)) as V;
  const u = ch.unit || "单位";
  return {
    hand,
    explain: frame.plane
      ? `手的位置：按下时手在 ${fmtValue(handStart)} + 指针的位移 (${moved.x.toFixed(1)}, ${moved.y.toFixed(1)}) px ÷ (${fmt(frame.ppu.x)}, ${fmt(frame.ppu.y)}) px/${u} = ${fmtValue(hand)}`
      : `手的位置：按下时手在 ${fmtValue(handStart)} + 指针沿运动方向的位移 ${moved.x.toFixed(1)} px ÷ ${fmt(frame.ppu.x)} px/${u} = ${fmtValue(hand)}`,
  };
}

/** 指针处对应的值：线上取指针沿运动方向的分量，不受边界限制。 */
export function pointerValue(frame: Frame, input: InputLayer): Value {
  return frame.valueOf(input.position());
}

/** 位置不出边界：每个分量限制在自己的范围里。 */
const clampValue = <V extends Value>(ch: ChannelDef<V>, x: V): V => valueOfVec(isPlane(ch), clampVec(vec(x), vec(ch.min), vec(ch.max))) as V;

/** 手：按住时位置 = 手的映射（手的位置），不出边界；没写映射时位置就是手的位置，越过边界停在边界上。 */
export function holdAt<V extends Value>(ch: ChannelDef<V>, hand: V, params: ParamTable): { x: V; line: string } {
  if (ch.handMap) {
    const mapped = ch.handMap.to(hand, params);
    const x = clampValue(ch, mapped);
    return { x, line: `手：位置 = 手的映射「${ch.handMap.name}」(手的位置 ${fmtValue(hand)}) = ${fmtValue(mapped)}${sameValue(x, mapped) ? "" : `，越过边界，停在边界 ${fmtValue(x)}`}（${ch.handMap.describe(params)}）` };
  }
  const x = clampValue(ch, hand);
  return { x, line: sameValue(x, hand) ? `手：位置 = 手的位置 ${fmtValue(x)}` : `手：手在 ${fmtValue(hand)}，越过边界，位置停在边界 ${fmtValue(x)}` };
}

/** 按下时手所在处：物体当前的位置按手的映射反推（没写映射时就是它）。 */
export function handFrom<V extends Value>(ch: ChannelDef<V>, x: V, params: ParamTable): V {
  return ch.handMap ? ch.handMap.from(x, params) : x;
}

const STEP_MS = 0.5;
const DT = STEP_MS / 1000;
/** 工程上限：最长推进 5 s，超过就按当时位置停下。 */
const MAX_MS = 5000;
/** 日志里最多记几次到点、碰边界。 */
const MAX_EVENTS = 12;
/** 第 n 步结束时离开始多久（s）：按整数步算，到位时间这类整数 ms 正好落在步上。 */
const timeOf = (n: number) => (n * STEP_MS) / 1000;
/** 平面上“经过放置点”的浮点容差：点离这一步的线段不到 1e-9 px（线上恒为 0）。 */
const ON_SEGMENT = 1e-9;

/** 输出映射点：值和对应的 px（按 x、再按 y 升序；线上就是按值升序）。 */
function pointsOf<V extends Value>(ch: ChannelDef<V>, ppu: Vec): { values: V[]; px: Vec[] } {
  const values = [...(ch.mappingPoints ?? [])].sort((a, b) => vec(a).x - vec(b).x || vec(a).y - vec(b).y);
  return { values, px: values.map((v) => toPx(ppu, v)) };
}

function relationsOf(ch: ChannelDef<Value>, ppu: Vec, params: ParamTable, start: State, target: Vec | null, toward: Toward | null, letGo: State | null, moving: number): Relations {
  const plane = isPlane(ch);
  const points = pointsOf(ch, ppu).px;
  return {
    points,
    nearest(x) {
      if (points.length === 0) return null;
      let best = points[0];
      let bestD = len(sub(x, best));
      for (const p of points) {
        const d = len(sub(x, p));
        if (d <= bestD) { best = p; bestD = d; }
      }
      return best;
    },
    params,
    label: (px) => fmtValue(fromPx(plane, ppu, px)),
    start,
    target,
    toward,
    letGo,
    moving,
  };
}

/** 特例点：这一步的位移经过放置点时停在点上，吸掉比例 r 的动能（从点上出发离开不算）。返回发生了什么，没到点返回 null。 */
function atPoint(prev: Vec, b: State, places: readonly Vec[], r: number, label: (px: Vec) => string, speed: (v: Vec) => string): string | null {
  if (same(b.x, prev)) return null;
  const seg = sub(b.x, prev);
  const L2 = dot(seg, seg);
  let hit: Vec | null = null;
  let best = Infinity;
  for (const p of places) {
    if (same(p, prev)) continue;
    const q = sub(p, prev);
    const along = dot(q, seg);
    if (along < 0 || along > L2) continue;
    if (Math.abs(q.x * seg.y - q.y * seg.x) > ON_SEGMENT * Math.sqrt(L2)) continue;
    if (along < best) { best = along; hit = p; }
  }
  if (hit === null) return null;
  const before = b.v;
  b.x = hit;
  b.v = scale(before, keep(r));
  return `到点 ${label(hit)}，吸掉 ${show(r * 100)}% 动能，速度 ${speed(before)} → ${speed(b.v)} px/s`;
}

/** 特例点：边界。每个分量不出范围；碰到时这个分量朝外的速度吸掉比例 r 的动能并反向，另一个分量不变。碰到了返回 true。 */
function atBound(lo: Vec, hi: Vec, b: State, r: number): boolean {
  const outX = (b.x.x <= lo.x && b.v.x < 0) || (b.x.x >= hi.x && b.v.x > 0);
  const outY = (b.x.y <= lo.y && b.v.y < 0) || (b.x.y >= hi.y && b.v.y > 0);
  b.x = clampVec(b.x, lo, hi);
  if (!outX && !outY) return false;
  const k = keep(r);
  b.v = { x: outX ? -b.v.x * k : b.v.x, y: outY ? -b.v.y * k : b.v.y };
  return true;
}

/** 场给的是哪一种。 */
type Kind = "加速度" | "速度" | "位置";
const kindOf = (o: FieldOutput): Kind => ("a" in o ? "加速度" : "v" in o ? "速度" : "位置");

/** 一段推进：同一串 0.5 ms 的步，可以一次推完，也可以推到要读的那一步为止、下次接着推，结果相同。 */
interface Run {
  /** 推到的那一步结束时的位置（px）。 */
  x: Vec;
  /** 推了几步。 */
  steps: number;
  /** 推完了：停下了，或者到了 5 s 的上限。 */
  done: boolean;
  /** 停下的原因；还没停（没推完，或 5 s 内没停）为 null。 */
  stop: string | null;
  xs: Vec[];
  vs: Vec[];
  /** 第 n 步（从 1 起）场给的是不是位置；不记轨迹时为空。 */
  gave: boolean[];
  events: string[];
  dropped: number;
  /** 推进中场给过哪几种（停下的判断也算）。 */
  kinds: Set<Kind>;
  /** 推到第 n 步为止；停下了、到了上限就不再推。 */
  to(n: number): void;
  /**
   * 出发后第 ms 毫秒场给的位置（px，不出边界）：给位置的场是开始以来的时间到位置的函数，这一步场给的是位置时，两步之间直接问场，不按直线插。
   * 读这一刻的位置和速度（接住、带向手时手又动了、手放开）用它，不受 0.5 ms 一步的影响（例如 Embla 的滚动体每 1000/60 ms 才换一次速度，换的那一刻落在两步之间）。
   * 只在记轨迹时用。
   */
  exact(ms: number): Vec;
}

/**
 * 按场推进（px 平面）。每一步（协议第 4.5 节“推进”）：
 *   场给加速度：速度 += 加速度 × dt（一步里速度反向、而这时静止下场为 0，就停在 0），位置 += 速度 × dt；
 *   场给速度：速度就是它，位置 += 速度 × dt；
 *   场给位置：位置就是它，速度 = 这一步的位移 ÷ dt。
 * 然后是特例点：给加速度、速度时是放置点和边界（吸能，边界还反向）；给位置时只是边界（每个分量不出范围）。
 * 停下：再推一步也不动（两个分量都看）。给加速度时：速度为 0 且场给的为 0，或在边界上速度为 0、场朝外；
 * 给速度时：场给的速度为 0，或在边界上、给的速度朝外；给位置时：下一步给的位置就是当前位置。
 * places 是特例点里的放置点（px）：有被带向的放置点（手或终态）时只有它，没有时是输出映射点。target、toward 是场读的被带向的放置点。
 * record 为 false 时不记轨迹和经过。返回时还没推，推几步由 Run.to 定。
 */
function run(ch: ChannelDef<Value>, ppu: Vec, params: ParamTable, field: Field, x0: Vec, v0: Vec,
  opts: { places: readonly Vec[]; placeName: string; target: Vec | null; toward: Toward | null; letGo?: State | null; moving?: number; record: boolean }): Run {
  const plane = isPlane(ch);
  const lo = toPx(ppu, ch.min);
  const hi = toPx(ppu, ch.max);
  const pointRatio = params.pointAbsorb.value;
  const boundRatio = params.boundAbsorb.value;
  const b: State = { x: clampVec(x0, lo, hi), v: v0 };
  const rel = relationsOf(ch, ppu, params, { x: b.x, v: b.v }, opts.target, opts.toward, opts.letGo ?? null, opts.moving ?? 0);
  const kinds = new Set<Kind>();
  /** 这一段的走法：一段开始时定下（协议第 4.7 节“阶段”）。 */
  const step = field.begin(rel);
  /** 第 n 步的场：读这一步结束时离开始多久。 */
  const fieldAt = (s: State, n: number) => {
    const o = step(s, timeOf(n));
    kinds.add(kindOf(o));
    return o;
  };
  const record = opts.record;
  const xs = record ? [b.x] : [];
  const vs = record ? [b.v] : [];
  const gave: boolean[] = record ? [false] : [];
  const events: string[] = [];
  const note = (n: number, text: string) => {
    if (!record) return;
    if (events.length < MAX_EVENTS) events.push(`第 ${ms(n * STEP_MS)} ms ${text}`);
    else r.dropped++;
  };
  /** 日志里的速度（px/s）：线上是数，平面上是 (x, y)。 */
  const speed = (v: Vec) => fmtValue(valueOfVec(plane, v));
  const onBound = (x: Vec) => x.x <= lo.x || x.x >= hi.x || (plane && (x.y <= lo.y || x.y >= hi.y));
  /** 每个不为 0 的分量都在它的边界上、朝外：推了也出不去。 */
  const outward = (f: Vec) => {
    const c = (fc: number, xc: number, l: number, h: number) => fc === 0 || (xc <= l && fc < 0) || (xc >= h && fc > 0);
    return !isZero(f) && c(f.x, b.x.x, lo.x, hi.x) && c(f.y, b.x.y, lo.y, hi.y);
  };
  /** 停下的判断算出的下一步的场：状态没变，下一步直接用它，不再算一次。 */
  let ahead: FieldOutput | null = null;
  /** 走完 n 步以后，再推一步动不动；不动就返回原因。 */
  const stopped = (n: number): string | null => {
    const o = fieldAt(b, n + 1);
    ahead = o;
    if ("x" in o) return same(o.x, b.x) ? "再推一步也不动：场给的位置就是当前位置" : null;
    const where = opts.places.some((p) => same(p, b.x)) ? `在${opts.placeName}上，` : onBound(b.x) ? "在边界上，" : "";
    if ("v" in o) {
      if (isZero(o.v)) return `${where}场给的速度为 0`;
      return outward(o.v) ? "在边界上，场给的速度朝外" : null;
    }
    if (!isZero(b.v)) return null;
    if (isZero(o.a)) return `${where}速度为 0、场为 0`;
    return outward(o.a) ? "在边界上，速度为 0、场朝外" : null;
  };
  let n = 0;
  let clamped = false;
  const maxN = MAX_MS / STEP_MS;
  const to = (last: number) => {
    while (r.stop === null && n < Math.min(last, maxN)) step1();
    r.x = b.x;
    r.steps = n;
    r.done = r.stop !== null || n >= maxN;
  };
  const step1 = () => {
    n++;
    const prevX = b.x;
    const v = b.v;
    const o = ahead ?? fieldAt(b, n);
    ahead = null;
    if ("x" in o) {
      const x = clampVec(o.x, lo, hi);
      const out = !same(x, o.x);
      if (out && !clamped) note(n, `场给的位置越过边界，停在边界 ${rel.label(x)}`);
      if (!out && clamped) note(n, "场给的位置回到边界里");
      clamped = out;
      b.x = x;
      b.v = { x: (x.x - prevX.x) / DT, y: (x.y - prevX.y) / DT };
    } else {
      let next = "v" in o ? o.v : add(v, scale(o.a, DT));
      if ("a" in o && !isZero(v) && dot(next, v) <= 0) {
        const rest = fieldAt({ x: b.x, v: ZERO }, n);
        if ("a" in rest ? isZero(rest.a) : "v" in rest && isZero(rest.v)) next = ZERO;
      }
      b.v = next;
      b.x = add(b.x, scale(b.v, DT));
      const hit = atPoint(prevX, b, opts.places, pointRatio, rel.label, speed);
      if (hit) note(n, hit);
      const vb = b.v;
      if (atBound(lo, hi, b, boundRatio)) note(n, `碰到边界 ${rel.label(b.x)}，吸掉 ${show(boundRatio * 100)}% 动能，速度 ${speed(vb)} → ${speed(b.v)} px/s`);
    }
    if (record) { xs.push(b.x); vs.push(b.v); gave.push("x" in o); }
    r.stop = stopped(n);
  };
  const exact = (ms: number): Vec => {
    const i = Math.min(xs.length - 1, Math.max(0, Math.floor(ms / STEP_MS)));
    const o = step({ x: xs[i], v: vs[i] }, ms / 1000);
    return "x" in o ? clampVec(o.x, lo, hi) : xs[i];
  };
  const r: Run = { x: b.x, steps: 0, done: false, stop: null, xs, vs, gave, events, dropped: 0, kinds, to, exact };
  r.stop = stopped(0);
  to(0);
  return r;
}

/** 一次推完。 */
function runAll(...a: Parameters<typeof run>): Run {
  const r = run(...a);
  r.to(Infinity);
  return r;
}

/** 一段按力场推进的运动（值单位）：放置点上一次变了以来。 */
export interface Motion<V extends Value = Value> {
  /** 推进时长；0 表示出发处就停着。按需推的读它就推到停下。 */
  readonly durationMs: number;
  /** 停下的位置（取整前）。按需推的读它就推到停下。 */
  readonly rest: V;
  /** 出发后第 ms 毫秒的位置、速度和加速度（值单位、值单位/秒、值单位/秒²）。加速度是这一步速度的变化 ÷ 步长。按需推的推到这一刻为止。 */
  at(ms: number): { x: V; v: V; a: V };
  /** 到出发后第 ms 毫秒停下了没有（推进时长不超过 ms）。按需推的推到这一刻为止。 */
  stopped(ms: number): boolean;
  /** 这一段开始时的解释，日志照写。一次推完的连停在哪、为什么停都写好了。 */
  lines: string[];
  /** 停下时补写的解释：一次推完的为空（已在 lines 里）；按需推的是特例点、经过和停下。 */
  endLines(): string[];
}

/** 被带向的放置点（值）：手或终态。 */
export interface Place<V extends Value = Value> {
  toward: Toward;
  value: V;
}

export interface FieldMotion<V extends Value = Value> extends Motion<V> {
  /** 被带向的放置点；没有时为 null（手放开后按力场走，推出落点）。 */
  place: Place<V> | null;
  /** 落点：停下的位置按步长取整。 */
  readonly landing: V;
  /** 有被带向的放置点时停下时到没到它；带向终态没到是场写错了，日志写出来，物体停在那里（协议第 4.5 节）。没有时为 true。 */
  readonly reached: boolean;
}

/** 算速度时往回看多久（ms）：场一直给位置时，速度是场给的位置在这一刻之前这一小段里的变化率。 */
const EXACT_BACK_MS = 0.001;

/**
 * 按记下的轨迹读：第 ms 毫秒的位置（两步之间按直线插）、速度和加速度；先推到这一刻所在的那一步（一次推完的不用再推）。
 * 这一步场给的是位置时，位置直接问场（Run.exact），速度是这一刻之前 0.001 ms 里位置的变化率（场在这一刻换速度时取换之前的）。
 */
function trajectory<V extends Value>(r: Run, plane: boolean, ppu: Vec, rest: () => V): Motion<V>["at"] {
  const zero = valueOfVec(plane, ZERO) as V;
  const val = (p: Vec) => fromPx(plane, ppu, p) as V;
  return (t) => {
    const i = Math.max(0, Math.floor(t / STEP_MS));
    r.to(i + 1);
    if (r.done && t >= r.steps * STEP_MS) return { x: rest(), v: zero, a: zero };
    const f = Math.max(0, t / STEP_MS - i);
    const x0 = r.xs[i];
    const x1 = r.xs[i + 1];
    const a = { x: (r.vs[i + 1].x - r.vs[i].x) / DT, y: (r.vs[i + 1].y - r.vs[i].y) / DT };
    if (r.gave[i + 1]) {
      const x = r.exact(t);
      if (t < EXACT_BACK_MS) return { x: val(x), v: val(r.vs[0]), a: val(a) };
      const back = r.exact(t - EXACT_BACK_MS);
      const k = 1000 / EXACT_BACK_MS;
      return { x: val(x), v: val({ x: (x.x - back.x) * k, y: (x.y - back.y) * k }), a: val(a) };
    }
    return { x: val({ x: x0.x + (x1.x - x0.x) * f, y: x0.y + (x1.y - x0.y) * f }), v: val(r.vs[i]), a: val(a) };
  };
}

/** 特例点的说明：按推进中场给过的种类写。 */
function specialLines(ch: ChannelDef<Value>, params: ParamTable, kinds: Set<Kind>, places: string | null): string[] {
  const lines: string[] = [];
  const range = `${fmtValue(ch.min)}–${fmtValue(ch.max)}`;
  if (kinds.has("加速度") || kinds.has("速度")) {
    if (places) lines.push(`特例点：到达${places}时停在点上，吸掉 ${show(params.pointAbsorb.value * 100)}% 动能（${cite(params.pointAbsorb)}）`);
    lines.push(`特例点：边界 ${range}，碰到时吸掉 ${show(params.boundAbsorb.value * 100)}% 动能（${cite(params.boundAbsorb)}）`);
  }
  if (kinds.has("位置")) lines.push(`特例点：场给位置，位置不出边界 ${range}`);
  return lines;
}

function stopLine(r: Run, rest: Value, durationMs: number): string {
  if (r.stop === null) return `停下：${MAX_MS} ms 内没停，按当时位置 ${fmtValue(rest)}`;
  return durationMs === 0 ? `停下：出发处就停着（${r.stop}）` : `停下：第 ${ms(durationMs)} ms 停在 ${fmtValue(rest)}（${r.stop}）`;
}

const passLines = (r: Run) => (r.events.length ? [`经过：${r.events.join("；")}${r.dropped ? `；另有 ${r.dropped} 次略` : ""}`] : []);

/** 没有几何时按每单位 1 px 算（场按 px 写）。 */
const UNIT_PPU: Vec = { x: 1, y: 1 };

const PLACE_NAMES: Readonly<Record<Toward, string>> = { 手: "手的位置", 终态: "终态" };

/** 一段运动的输入：通道、几何、参数、力场，从 x0、v0（值单位）出发，被带向的放置点，和这一段从手放开开始时放开那一刻的手、物体这次被力场连续带着动了多久（s）。 */
export type MotionArgs<V extends Value = Value> = [ch: ChannelDef<V>, frame: Frame | null, params: ParamTable, field: Field, x0: V, v0: V,
  place: Place<V> | null, opts?: { letGo?: { x: V; v: V } | null; moving?: number }];

/**
 * 一段运动（原始执行）：从位置 x0、速度 v0（值单位）出发，按力场推进到停下，这一段开始时整段推完。
 * 有被带向的放置点（手或终态）时场读它，特例点里的放置点只有它；没有时特例点是输出映射点，停下的位置按步长取整就是落点。
 * letGo 是这一段从手放开开始时放开那一刻的手，moving 是物体这次被力场连续带着动了多久（s）。
 * 没有几何时：没有被带向的放置点就不推进，停在出发处；带向终态照样带（宿主值变了，没有几何的绑定也要过去），场按每单位 1 px 算，日志写明。
 */
export function fieldMotion<V extends Value>(...a: MotionArgs<V>): FieldMotion<V> {
  return motionOf(true, ...a);
}

/**
 * 一段运动（优化，协议第 4.7 节）：和 fieldMotion 是同一串步，只推到要读的时刻为止，下次接着推；结果相同。
 * 日志里特例点、经过和停下在真的停下时写（FieldMotion.endLines）。
 */
export function fieldMotionOnDemand<V extends Value>(...a: MotionArgs<V>): FieldMotion<V> {
  return motionOf(false, ...a);
}

function motionOf<V extends Value>(whole: boolean, ch: ChannelDef<V>, frame: Frame | null, params: ParamTable, field: Field, x0: V, v0: V,
  place: Place<V> | null, opts: { letGo?: { x: V; v: V } | null; moving?: number } = {}): FieldMotion<V> {
  const unitName = ch.unit || "单位";
  const plane = isPlane(ch);
  const moving = opts.moving ?? 0;
  if (!frame && place === null) {
    const landing = stepValue(ch, x0);
    const zero = valueOfVec(plane, ZERO) as V;
    return { durationMs: 0, rest: x0, place, landing, reached: true, at: () => ({ x: x0, v: zero, a: zero }), stopped: () => true,
      lines: [`力场：没有几何，不推进，停在 ${fmtValue(x0)}`, `落点 ${fmtValue(landing)}`], endLines: () => [] };
  }
  const ppu = frame ? frame.ppu : UNIT_PPU;
  const v0px = toPx(ppu, v0);
  const target = place ? toPx(ppu, place.value) : null;
  const letGo = opts.letGo ? { x: toPx(ppu, opts.letGo.x), v: toPx(ppu, opts.letGo.v) } : null;
  const r = run(ch, ppu, params, field, toPx(ppu, x0), v0px, {
    places: target ? [target] : pointsOf(ch, ppu).px, placeName: place ? PLACE_NAMES[place.toward] : "输出映射点",
    target, toward: place ? place.toward : null, letGo, moving, record: true,
  });
  if (whole) r.to(Infinity);
  /** 停下以后的结果：推完才有，第一次读时推完。 */
  let end: { rest: V; landing: V; durationMs: number; reached: boolean } | null = null;
  const ended = () => {
    if (end) return end;
    r.to(Infinity);
    const rest = valueAt(ch, ppu, r.x, place ? [place.value] : []);
    end = { rest, landing: stepValue(ch, rest), durationMs: r.steps * STEP_MS, reached: place === null || sameValue(rest, place.value) };
    return end;
  };
  const how = [moving > 0 ? `接着动，已动 ${ms(moving * 1000)} ms` : "", letGo ? "这一段从手放开开始" : ""].filter(Boolean).join("；");
  const lines = [`从 ${fmtValue(x0)}、速度 ${fmtValue(v0)} ${unitName}/s 出发${place ? `，带向${PLACE_NAMES[place.toward]} ${fmtValue(place.value)}` : ""}${how ? `（${how}）` : ""}`, `力场「${field.name}」`];
  if (!frame) lines.push(`没有几何：场按每${unitName} 1 px 算`);
  for (const d of field.describe(params)) lines.push(`力场：${d}`);
  /** 特例点、经过、停下（带向手的不写）。 */
  const outcome = (): string[] => {
    if (place?.toward === "手") return [];
    const e = ended();
    const out = specialLines(ch, params, r.kinds, place ? "终态" : ch.mappingPoints?.length ? "输出映射点" : null);
    if (!place && field.params.includes("kineticFriction") && !isZero(v0px)) {
      const slide = scale(unit(v0px), len(v0px) ** 2 / (2 * params.kineticFriction.value));
      out.push(`对照：只有摩擦时会滑到 ${fmtValue(fromPx(plane, ppu, add(toPx(ppu, x0), slide)))}`);
    }
    out.push(...passLines(r), stopLine(r, e.rest, e.durationMs));
    if (!place) {
      const stepText = ch.step === undefined ? "" : fmtValue(ch.step);
      out.push(`落点 ${fmtValue(e.landing)}${ch.step ? `（停下的位置按步长 ${stepText}${ch.unit ? ` ${ch.unit}` : ""} 取整）` : ""}`);
    } else if (!e.reached) out.push(`错误：力场停下时没到终态 ${fmtValue(place.value)}，停在 ${fmtValue(e.rest)}；不再重新到终态`);
    return out;
  };
  if (whole) lines.push(...outcome());
  else if (place?.toward !== "手") lines.push("停下：按需推进，停下时写");
  return {
    place,
    get durationMs() { return ended().durationMs; },
    get rest() { return ended().rest; },
    get landing() { return ended().landing; },
    get reached() { return ended().reached; },
    at: trajectory(r, plane, ppu, () => ended().rest),
    stopped: (t) => {
      r.to(Math.floor(t / STEP_MS));
      return r.done && r.steps * STEP_MS <= t;
    },
    lines,
    endLines: () => (whole ? [] : outcome()),
  };
}

/** px 位置对应的值：每个分量正好在边界、输出映射点或 exact 里的值上时取那个值本身，不经除法带出舍入误差。 */
function valueAt<V extends Value>(ch: ChannelDef<V>, ppu: Vec, x: Vec, exact: readonly V[]): V {
  const plane = isPlane(ch);
  const known = [ch.min, ch.max, ...(ch.mappingPoints ?? []), ...exact].map(vec);
  const pick = (px: number, k: number, axis: "x" | "y") => {
    for (const q of known) if (px === q[axis] * k) return q[axis];
    return px / k;
  };
  return valueOfVec(plane, { x: pick(x.x, ppu.x, "x"), y: plane ? pick(x.y, ppu.y, "y") : 0 }) as V;
}

/** 在 x 处静止松手会停到哪个输出映射点（值）；停在别处返回 null。所在段和吸附范围的推论都用它。 */
export function restPoint<V extends Value>(ch: ChannelDef<V>, frame: Frame, params: ParamTable, field: Field, x: V): V | null {
  const { values, px } = pointsOf(ch, frame.ppu);
  if (values.length === 0) return null;
  const end = runAll(ch, frame.ppu, params, field, toPx(frame.ppu, x), ZERO, { places: px, placeName: "输出映射点", target: null, toward: null, record: false }).x;
  const i = px.findIndex((p) => same(p, end));
  return i >= 0 ? values[i] : null;
}

/** 一个输出映射点沿一条轴一侧的静止吸附范围：px 是范围有多远；open 为 false 表示点在这一侧的边界上，不计。 */
export interface RestSide {
  /** 朝哪边：值空间里的单位方向，线上只有 (−1, 0) 和 (1, 0)。 */
  dir: Vec;
  px: number;
  open: boolean;
}

/**
 * 每个输出映射点的静止吸附范围：在其中静止松手会停到这个点。是力场的推论，不是参数。
 * 沿每条轴的两侧各量一次（线：−、+；平面：−x、+x、−y、+y），到和相邻点的中垂线（线上就是到相邻两点的中点）或边界为止，
 * 在其中二分，假定范围是一段。
 */
export function restRanges<V extends Value>(ch: ChannelDef<V>, frame: Frame, params: ParamTable, field: Field): { point: V; sides: RestSide[] }[] {
  const ppu = frame.ppu;
  const plane = isPlane(ch);
  const { values, px } = pointsOf(ch, ppu);
  const lo = toPx(ppu, ch.min);
  const hi = toPx(ppu, ch.max);
  const dirs: Vec[] = plane ? [{ x: -1, y: 0 }, { x: 1, y: 0 }, { x: 0, y: -1 }, { x: 0, y: 1 }] : [{ x: -1, y: 0 }, { x: 1, y: 0 }];
  return values.map((point, i) => {
    const p = px[i];
    const side = (d: Vec): RestSide => {
      // 边界：沿 d 走多远出界；相邻点 q：沿 d 走到 p、q 的中垂线的距离 |q − p|² ÷ (2·(q − p)·d)
      let limit = d.x < 0 ? p.x - lo.x : d.x > 0 ? hi.x - p.x : d.y < 0 ? p.y - lo.y : hi.y - p.y;
      for (let j = 0; j < px.length; j++) {
        if (j === i) continue;
        const q = sub(px[j], p);
        const toward = dot(q, d);
        if (toward > 0) limit = Math.min(limit, dot(q, q) / (2 * toward));
      }
      if (!(limit > 0)) return { dir: d, px: 0, open: false };
      const ok = (dist: number) => {
        const end = runAll(ch, ppu, params, field, add(p, scale(d, dist)), ZERO, { places: px, placeName: "输出映射点", target: null, toward: null, record: false }).x;
        return same(end, p);
      };
      if (ok(limit * 0.999999)) return { dir: d, px: limit, open: true };
      let good = 0;
      let bad = limit;
      for (let k = 0; k < 18; k++) {
        const mid = (good + bad) / 2;
        if (ok(mid)) good = mid;
        else bad = mid;
      }
      return { dir: d, px: good, open: true };
    };
    return { point, sides: dirs.map(side) };
  });
}

/** 一项加速度（值单位/秒²）：力场或手。 */
export interface Force<V extends Value = Value> {
  name: string;
  a: V;
}

/**
 * 力场在状态 s（值单位）处给的加速度。since 是这段运动开始以来的时长（s）和开始时的状态；手带着时力场照常算、被手盖过，从这一刻算起（默认）。
 * 场直接给速度时，是这一步把速度改到它所需的加速度：速度的变化 ÷ 步长；直接给位置时，按这一步的位移算出速度，再同样换成加速度。
 * 特例点（放置点、边界的吸能）不是场，不算在里面。
 */
export function fieldForce<V extends Value>(ch: ChannelDef<V>, frame: Frame, params: ParamTable, field: Field, s: { x: V; v: V },
  since: { t: number; start: { x: V; v: V } } = { t: 0, start: s }): Force<V> {
  const ppu = frame.ppu;
  const px = (q: { x: V; v: V }): State => ({ x: toPx(ppu, q.x), v: toPx(ppu, q.v) });
  const now = px(s);
  const o = field.begin(relationsOf(ch, ppu, params, px(since.start), null, null, null, 0))(now, since.t);
  const next = "a" in o ? null : "v" in o ? o.v : { x: (o.x.x - now.x.x) / DT, y: (o.x.y - now.x.y) / DT };
  const a = next === null ? (o as { a: Vec }).a : { x: (next.x - now.v.x) / DT, y: (next.y - now.v.y) / DT };
  return { name: "力场", a: fromPx(isPlane(ch), ppu, a) as V };
}
