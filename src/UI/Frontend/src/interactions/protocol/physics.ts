// Channel motion tracks position and velocity under one field.
// Hand control follows the bounded hand map directly, or uses the mapped hand as a field target.
// Other motion follows a terminal target or predicts release landing.
// Fixed 0.5ms steps make results independent of frame rate; retarget from current position and velocity.
// Eager and on-demand execution use the same step sequence.
// Release, placement-point, and boundary absorption remove configured kinetic-energy fractions.
// Position-supplying fields are only boundary-clamped; geometry locates the logical center.
// Advance in pixel vectors, converting each axis by its pixel scale.
// External values remain scalar for lines or points for planes.

import { keep } from "./field.ts";
import type { Field, FieldOutput, Relations, State, Toward } from "./field.ts";
import type { ChannelDef, Geometry, Point } from "./definition.ts";
import type { InputLayer } from "./input.ts";
import type { ParamTable } from "./params.ts";
import { cite, show } from "./params.ts";
import { add, clampVec, dot, fmt, fmtValue, isZero, len, same, sameValue, scale, sub, unit, valueOfVec, vec, ZERO } from "./vec.ts";
import type { Value, Vec } from "./vec.ts";

/** Format fixed-step milliseconds with at most one decimal. */
export const ms = (n: number) => String(Number(n.toFixed(1)));

/** A point-valued minimum identifies a plane channel. */
export const isPlane = (ch: ChannelDef<Value>): boolean => typeof ch.min !== "number";

/** Step-round call values per axis without quantizing drawing. */
export function stepValue<V extends Value>(ch: ChannelDef<V>, x: V): V {
  if (!ch.step) return x;
  const st = vec(ch.step);
  const p = vec(x);
  const r = (v: number, s: number) => (s ? Math.round(v / s) * s : v);
  return valueOfVec(isPlane(ch), { x: r(p.x, st.x), y: r(p.y, st.y) }) as V;
}

/** Linear geometry from viewport centers at the channel bounds; lines follow the endpoint axis and planes use rectangle corners. */
export interface Frame {
  plane: boolean;
  /** Pixels per value unit on each axis; the unused line axis has scale 1. */
  ppu: Vec;
  /** Unit viewport directions for increasing values; the unused line axis is perpendicular. */
  dirX: Point;
  dirY: Point;
  /** Viewport center for a value, including out-of-bounds values. */
  at(v: Value): Point;
  /** Invert viewport position without clamping; a line projects onto its motion axis. */
  valueOf(p: Point): Value;
  /** Convert value-unit velocity or acceleration to a viewport pixel vector. */
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

/** Scale each value component into the pixel plane. */
const toPx = (ppu: Vec, v: Value): Vec => { const u = vec(v); return { x: u.x * ppu.x, y: u.y * ppu.y }; };
/** Convert pixel position to channel value, preserving special values through valueAt. */
const fromPx = (plane: boolean, ppu: Vec, p: Vec): Value => valueOfVec(plane, { x: p.x / ppu.x, y: plane ? p.y / ppu.y : 0 });

/** Hand position is the press anchor plus projected pointer displacement. Grabbing preserves offset; jump-to-pointer uses the pointer anchor. No boundary clamp. */
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

/** Pointer value projected onto the channel axis without clamping. */
export function pointerValue(frame: Frame, input: InputLayer): Value {
  return frame.valueOf(input.position());
}

/** Clamp each position component to its bounds. */
const clampValue = <V extends Value>(ch: ChannelDef<V>, x: V): V => valueOfVec(isPlane(ch), clampVec(vec(x), vec(ch.min), vec(ch.max))) as V;

/** Map the hand, then clamp; identity is the default map. */
export function holdAt<V extends Value>(ch: ChannelDef<V>, hand: V, params: ParamTable): { x: V; line: string } {
  if (ch.handMap) {
    const mapped = ch.handMap.to(hand, params);
    const x = clampValue(ch, mapped);
    return { x, line: `手：位置 = 手的映射「${ch.handMap.name}」(手的位置 ${fmtValue(hand)}) = ${fmtValue(mapped)}${sameValue(x, mapped) ? "" : `，越过边界，停在边界 ${fmtValue(x)}`}（${ch.handMap.describe(params)}）` };
  }
  const x = clampValue(ch, hand);
  return { x, line: sameValue(x, hand) ? `手：位置 = 手的位置 ${fmtValue(x)}` : `手：手在 ${fmtValue(hand)}，越过边界，位置停在边界 ${fmtValue(x)}` };
}

/** Invert the hand map at the current object position to obtain the press anchor. */
export function handFrom<V extends Value>(ch: ChannelDef<V>, x: V, params: ParamTable): V {
  return ch.handMap ? ch.handMap.from(x, params) : x;
}

const STEP_MS = 0.5;
const DT = STEP_MS / 1000;
/** Bound simulation to five seconds, then stop at the current position. */
const MAX_MS = 5000;
/** Maximum placement and boundary events retained in traces. */
const MAX_EVENTS = 12;
/** Compute elapsed seconds from integer steps so integer-millisecond targets align exactly. */
const timeOf = (n: number) => (n * STEP_MS) / 1000;
/** Pixel tolerance for a plane segment passing a placement point. */
const ON_SEGMENT = 1e-9;

/** Output mapping values and pixel positions sorted by x then y. */
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

/** On crossing a placement point, stop there and absorb fraction r of kinetic energy; departure from the point does not count. */
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

/** Clamp each axis and reverse its outward velocity after energy absorption; preserve the other component. */
function atBound(lo: Vec, hi: Vec, b: State, r: number): boolean {
  const outX = (b.x.x <= lo.x && b.v.x < 0) || (b.x.x >= hi.x && b.v.x > 0);
  const outY = (b.x.y <= lo.y && b.v.y < 0) || (b.x.y >= hi.y && b.v.y > 0);
  b.x = clampVec(b.x, lo, hi);
  if (!outX && !outY) return false;
  const k = keep(r);
  b.v = { x: outX ? -b.v.x * k : b.v.x, y: outY ? -b.v.y * k : b.v.y };
  return true;
}

/** Field output kind. */
type Kind = "加速度" | "速度" | "位置";
const kindOf = (o: FieldOutput): Kind => ("a" in o ? "加速度" : "v" in o ? "速度" : "位置");

/** One fixed-step run, advanced eagerly or incrementally with identical results. */
interface Run {
  /** Pixel position at the end of the latest step. */
  x: Vec;
  /** Completed step count. */
  steps: number;
  /** Run ended by rest or the five-second limit. */
  done: boolean;
  /** Stop reason, or null until rest is reached. */
  stop: string | null;
  xs: Vec[];
  vs: Vec[];
  /** Whether each one-based step supplies position; empty when not recording. */
  gave: boolean[];
  events: string[];
  dropped: number;
  /** Field output kinds encountered, including stop checks. */
  kinds: Set<Kind>;
  /** Advance through step n unless stopped or at the limit. */
  to(n: number): void;
  /** For recorded position fields, evaluate exact position between steps instead of interpolating, preserving retargeting continuity. */
  exact(ms: number): Vec;
}

/** Advance acceleration, velocity, or position output, then apply placement absorption and boundaries. Stop when another step cannot move either axis. Recording is optional; Run.to controls advancement. */
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
  /** Segment behavior fixed at its start. */
  const step = field.begin(rel);
  /** Evaluate the field at the end time of step n. */
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
  /** Format pixel velocity as scalar or plane values. */
  const speed = (v: Vec) => fmtValue(valueOfVec(plane, v));
  const onBound = (x: Vec) => x.x <= lo.x || x.x >= hi.x || (plane && (x.y <= lo.y || x.y >= hi.y));
  /** Every nonzero component points outward at its boundary, so motion is blocked. */
  const outward = (f: Vec) => {
    const c = (fc: number, xc: number, l: number, h: number) => fc === 0 || (xc <= l && fc < 0) || (xc >= h && fc > 0);
    return !isZero(f) && c(f.x, b.x.x, lo.x, hi.x) && c(f.y, b.x.y, lo.y, hi.y);
  };
  /** Reuse the stop-check field output because the state has not changed. */
  let ahead: FieldOutput | null = null;
  /** Check whether another step moves after n steps; return a reason if blocked. */
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

/** Advance the entire run. */
function runAll(...a: Parameters<typeof run>): Run {
  const r = run(...a);
  r.to(Infinity);
  return r;
}

/** Field-driven motion in value units since the target last changed. */
export interface Motion<V extends Value = Value> {
  /** Motion duration; reading it completes an on-demand run. Zero means initially stopped. */
  readonly durationMs: number;
  /** Unrounded stop position; reading it completes an on-demand run. */
  readonly rest: V;
  /** Position, velocity, and acceleration at ms in value units; advance on demand only to that time. */
  at(ms: number): { x: V; v: V; a: V };
  /** Whether motion has stopped by ms; advance on demand to that time. */
  stopped(ms: number): boolean;
  /** Initial trace explanation; eager execution also includes its final result. */
  lines: string[];
  /** Deferred final trace lines; empty for eager execution. */
  endLines(): string[];
}

/** Hand or terminal target in value units. */
export interface Place<V extends Value = Value> {
  toward: Toward;
  value: V;
}

export interface FieldMotion<V extends Value = Value> extends Motion<V> {
  /** Target, or null for unconstrained release prediction. */
  place: Place<V> | null;
  /** Step-rounded stop position. */
  readonly landing: V;
  /** Whether motion reached its target; true without one. A missed terminal target indicates an invalid field. */
  readonly reached: boolean;
}

/** Backward time window for velocity derived from position fields, in milliseconds. */
const EXACT_BACK_MS = 0.001;

/** Read recorded motion, interpolating step positions unless the field supplies exact positions. Position-field velocity uses a 0.001ms backward difference. */
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

/** Explain special-point rules for the encountered field output kinds. */
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

/** Without geometry, use one pixel per value unit. */
const UNIT_PPU: Vec = { x: 1, y: 1 };

const PLACE_NAMES: Readonly<Record<Toward, string>> = { 手: "手的位置", 终态: "终态" };

/** Segment inputs: channel, geometry, parameters, field, initial state, target, release hand state, and continuous motion time. */
export type MotionArgs<V extends Value = Value> = [ch: ChannelDef<V>, frame: Frame | null, params: ParamTable, field: Field, x0: V, v0: V,
  place: Place<V> | null, opts?: { letGo?: { x: V; v: V } | null; moving?: number }];

/** Eagerly simulate a segment to rest. Targeted motion uses only its target as a placement point; untargeted motion uses output points. Without geometry, only targeted motion advances. */
export function fieldMotion<V extends Value>(...a: MotionArgs<V>): FieldMotion<V> {
  return motionOf(true, ...a);
}

/** Advance the same steps lazily to each requested time; final special-point and stop traces are deferred until rest. */
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
  /** Final result, completing the run on first read. */
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
  /** Special-point, crossing, and stop traces, omitted while targeting the hand. */
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

/** Preserve exact boundary, mapping-point, and supplied values instead of introducing division rounding. */
function valueAt<V extends Value>(ch: ChannelDef<V>, ppu: Vec, x: Vec, exact: readonly V[]): V {
  const plane = isPlane(ch);
  const known = [ch.min, ch.max, ...(ch.mappingPoints ?? []), ...exact].map(vec);
  const pick = (px: number, k: number, axis: "x" | "y") => {
    for (const q of known) if (px === q[axis] * k) return q[axis];
    return px / k;
  };
  return valueOfVec(plane, { x: pick(x.x, ppu.x, "x"), y: plane ? pick(x.y, ppu.y, "y") : 0 }) as V;
}

/** Output mapping point reached from stationary release at x, or null when stopping elsewhere. */
export function restPoint<V extends Value>(ch: ChannelDef<V>, frame: Frame, params: ParamTable, field: Field, x: V): V | null {
  const { values, px } = pointsOf(ch, frame.ppu);
  if (values.length === 0) return null;
  const end = runAll(ch, frame.ppu, params, field, toPx(frame.ppu, x), ZERO, { places: px, placeName: "输出映射点", target: null, toward: null, record: false }).x;
  const i = px.findIndex((p) => same(p, end));
  return i >= 0 ? values[i] : null;
}

/** Attraction distance on one side of an axis; open=false denotes a boundary with no range. */
export interface RestSide {
  /** Unit direction in value space; lines use only (-1, 0) and (1, 0). */
  dir: Vec;
  px: number;
  open: boolean;
}

/** Derive stationary attraction ranges by bisecting each axis side up to a boundary or neighbor bisector, assuming a contiguous range. */
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
      // Limit each direction by the boundary or the perpendicular bisector to a neighboring point.
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

/** One acceleration contribution in value units per second squared. */
export interface Force<V extends Value = Value> {
  name: string;
  a: V;
}

/** Field acceleration at a value-unit state. Convert velocity/position outputs through the step duration; exclude special-point absorption. since supplies segment time and initial state. */
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
