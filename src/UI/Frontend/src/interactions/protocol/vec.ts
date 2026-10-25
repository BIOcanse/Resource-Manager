// 平面（协议第 4.5 节“平面”）：通道有一条轴（线）或两条轴（平面）。物体的位置、速度，场给的量，都是平面上的向量；
// 线就是只有第一条轴的平面，第二个分量恒为 0。通道的值：一条轴时是数，两条轴时是点。

/** 平面上的向量（px 或值单位，看用在哪里）。 */
export interface Vec { readonly x: number; readonly y: number }

/** 通道的值：一条轴时是数，两条轴时是点 (x, y)。 */
export type Value = number | Vec;

export const ZERO: Vec = Object.freeze({ x: 0, y: 0 });

/** 值 → 向量：一条轴的值是 (值, 0)。 */
export const vec = (v: Value): Vec => (typeof v === "number" ? { x: v, y: 0 } : v);
/** 向量 → 值：plane 为 false（线）时取第一个分量。 */
export const valueOfVec = (plane: boolean, v: Vec): Value => (plane ? { x: v.x, y: v.y } : v.x);

export const add = (a: Vec, b: Vec): Vec => ({ x: a.x + b.x, y: a.y + b.y });
export const sub = (a: Vec, b: Vec): Vec => ({ x: a.x - b.x, y: a.y - b.y });
export const scale = (a: Vec, k: number): Vec => ({ x: a.x * k, y: a.y * k });
export const dot = (a: Vec, b: Vec): number => a.x * b.x + a.y * b.y;
export const len = (a: Vec): number => Math.hypot(a.x, a.y);
export const same = (a: Vec, b: Vec): boolean => a.x === b.x && a.y === b.y;
export const isZero = (a: Vec): boolean => a.x === 0 && a.y === 0;
/** 单位向量（a 不为 0）。正好在横轴上时按符号取，线上的结果和一维的算法逐位相同。 */
export const unit = (a: Vec): Vec => (a.y === 0 ? { x: Math.sign(a.x), y: 0 } : a.x === 0 ? { x: 0, y: Math.sign(a.y) } : scale(a, 1 / len(a)));
/** 每个分量各自限制在 [lo, hi]。 */
export const clampVec = (v: Vec, lo: Vec, hi: Vec): Vec => ({ x: Math.min(hi.x, Math.max(lo.x, v.x)), y: Math.min(hi.y, Math.max(lo.y, v.y)) });

/** 两个值相同：线上是数相等，平面上两个分量都相等。 */
export const sameValue = (a: Value, b: Value): boolean => same(vec(a), vec(b));

/** 日志里的数：绝对值 100 以上留 1 位小数，否则最多 3 位；极小的负数写 0，不写 -0。 */
export const fmt = (n: number): string => {
  const s = Math.abs(n) >= 100 ? n.toFixed(1) : n.toFixed(3).replace(/\.?0+$/, "");
  return s === "-0" ? "0" : s;
};
/** 日志里的值：线上是数，平面上是 (x, y)。 */
export const fmtValue = (v: Value): string => (typeof v === "number" ? fmt(v) : `(${fmt(v.x)}, ${fmt(v.y)})`);
