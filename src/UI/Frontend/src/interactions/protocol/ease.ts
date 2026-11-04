// CSS cubic-bezier maps normalized time to progress.

/** Standard CSS ease and ease-out curves. */
export const CSS_EASE = [0.25, 0.1, 0.25, 1] as const;
export const CSS_EASE_OUT = [0, 0, 0.58, 1] as const;

/** Solve x(s)=time with Newton iteration, falling back to bisection, then return y(s). */
export function cubicBezier(x1: number, y1: number, x2: number, y2: number): (t: number) => number {
  const bx = (s: number) => 3 * x1 * s * (1 - s) ** 2 + 3 * x2 * s * s * (1 - s) + s ** 3;
  const by = (s: number) => 3 * y1 * s * (1 - s) ** 2 + 3 * y2 * s * s * (1 - s) + s ** 3;
  const dbx = (s: number) => 3 * x1 * (1 - s) ** 2 + 6 * (x2 - x1) * s * (1 - s) + 3 * (1 - x2) * s * s;
  return (t) => {
    if (t <= 0) return 0;
    if (t >= 1) return 1;
    let s = t;
    for (let i = 0; i < 8; i++) {
      const d = dbx(s);
      if (Math.abs(d) < 1e-6) break;
      s -= (bx(s) - t) / d;
    }
    if (!(s >= 0 && s <= 1) || Math.abs(bx(s) - t) > 1e-6) {
      let lo = 0;
      let hi = 1;
      for (let i = 0; i < 40; i++) {
        s = (lo + hi) / 2;
        if (bx(s) < t) lo = s; else hi = s;
      }
    }
    return by(s);
  };
}
