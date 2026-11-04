// Channels use scalar line values or two-axis plane values.
// A line occupies the first axis with a zero second component.

/** Two-axis vector in pixels or channel units. */
export interface Vec { readonly x: number; readonly y: number }

/** Scalar line value or plane point (x, y). */
export type Value = number | Vec;

export const ZERO: Vec = Object.freeze({ x: 0, y: 0 });

/** Embed a scalar as (value, 0). */
export const vec = (v: Value): Vec => (typeof v === "number" ? { x: v, y: 0 } : v);
/** Extract the first component for a line. */
export const valueOfVec = (plane: boolean, v: Vec): Value => (plane ? { x: v.x, y: v.y } : v.x);

export const add = (a: Vec, b: Vec): Vec => ({ x: a.x + b.x, y: a.y + b.y });
export const sub = (a: Vec, b: Vec): Vec => ({ x: a.x - b.x, y: a.y - b.y });
export const scale = (a: Vec, k: number): Vec => ({ x: a.x * k, y: a.y * k });
export const dot = (a: Vec, b: Vec): number => a.x * b.x + a.y * b.y;
export const len = (a: Vec): number => Math.hypot(a.x, a.y);
export const same = (a: Vec, b: Vec): boolean => a.x === b.x && a.y === b.y;
export const isZero = (a: Vec): boolean => a.x === 0 && a.y === 0;
/** Unit vector for nonzero a; preserve exact scalar-axis arithmetic. */
export const unit = (a: Vec): Vec => (a.y === 0 ? { x: Math.sign(a.x), y: 0 } : a.x === 0 ? { x: 0, y: Math.sign(a.y) } : scale(a, 1 / len(a)));
/** Clamp each component to [lo, hi]. */
export const clampVec = (v: Vec, lo: Vec, hi: Vec): Vec => ({ x: Math.min(hi.x, Math.max(lo.x, v.x)), y: Math.min(hi.y, Math.max(lo.y, v.y)) });

/** Exact scalar or componentwise plane equality. */
export const sameValue = (a: Value, b: Value): boolean => same(vec(a), vec(b));

/** Trace numbers use one decimal above magnitude 100, otherwise up to three; normalize negative zero. */
export const fmt = (n: number): string => {
  const s = Math.abs(n) >= 100 ? n.toFixed(1) : n.toFixed(3).replace(/\.?0+$/, "");
  return s === "-0" ? "0" : s;
};
/** Format scalar or plane values for traces. */
export const fmtValue = (v: Value): string => (typeof v === "number" ? fmt(v) : `(${fmt(v.x)}, ${fmt(v.y)})`);
