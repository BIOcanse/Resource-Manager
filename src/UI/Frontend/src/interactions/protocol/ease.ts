// CSS 的 cubic-bezier 计时函数：时间比例 → 进度。可复用的场“缓动”和复刻的原版都用它（同一个 CSS 规范的函数）。

/** CSS 的关键字曲线：ease、ease-out（CSS Easing Functions 规范的值）。复刻的 Sonner、react-switch、Ionic 下拉刷新用它们。 */
export const CSS_EASE = [0.25, 0.1, 0.25, 1] as const;
export const CSS_EASE_OUT = [0, 0, 0.58, 1] as const;

/** 先按 x(s) = 时间比例 用牛顿法解 s（不收敛时二分），再取 y(s)。 */
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
