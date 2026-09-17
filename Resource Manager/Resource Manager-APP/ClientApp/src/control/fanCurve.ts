import type { ControlCurvePoint } from "./controlTypes.ts";

/**
 * 风扇曲线：温度到转速。
 *
 * **调整点是固定的，每 5 度一个**，用户只调每个点的转速，不去拖动曲线本身。
 * 这样每次调整都落在同一组温度上，可预期、可对比、也不会拖出一堆密集又无意义的点；
 * 曲线的平滑由算法负责，不靠手抖。
 *
 * 纯函数，不碰界面也不碰硬件 —— 不管最后由 NBFC 还是显卡驱动去写，规矩是一样的。
 */

/** 转速百分比的合法范围。 */
export const minimumPercent = 0;
export const maximumPercent = 100;

/**
 * 固定的调整点。
 *
 * 40 度以下风扇基本不用转，85 度以上该满转了，中间每 5 度一档 —— 十个点，
 * 既够表达一条曲线，又不至于多到没人愿意逐个调。
 */
export const curveStops: readonly number[] =
  [40, 45, 50, 55, 60, 65, 70, 75, 80, 85];

function clamp(value: number, low: number, high: number) {
  return value < low ? low : value > high ? high : value;
}

function sanitizePercent(value: number): number {
  return Number.isFinite(value) ? Math.round(clamp(value, minimumPercent, maximumPercent)) : 0;
}

/** 一条温和的默认曲线，新建时用它起步。 */
export function defaultStops(): number[] {
  return [0, 15, 25, 35, 45, 55, 70, 85, 95, 100];
}

/**
 * 把存下来的曲线读成每个调整点的转速。
 *
 * 存的点不一定正好落在这组温度上（比如早先存的、或者别处导入的），
 * 所以按线性插值取值，而不是要求完全对齐 —— 对不齐就丢掉用户设过的东西是不能接受的。
 */
export function stopsFromCurve(points: readonly ControlCurvePoint[] | null | undefined): number[] {
  const curve = (points ?? [])
    .filter((point) =>
      Number.isFinite(point.temperatureCelsius) && Number.isFinite(point.percent))
    .map((point) => ({
      temperatureCelsius: point.temperatureCelsius,
      percent: sanitizePercent(point.percent)
    }))
    .sort((left, right) => left.temperatureCelsius - right.temperatureCelsius);

  if (curve.length === 0) {
    return curveStops.map(() => 0);
  }
  // 插值出来的是小数，档位必须是整数百分比 —— 否则界面上会出现
  // 38.33333333333333 这种读数，而且用户也没法调到那个值上去。
  return curveStops.map((temperature) => Math.round(interpolate(curve, temperature)));
}

/** 每个调整点的转速 → 可以存下去的曲线点。 */
export function curveFromStops(percents: readonly number[]): ControlCurvePoint[] {
  return curveStops.map((temperatureCelsius, index) => ({
    temperatureCelsius,
    percent: sanitizePercent(percents[index] ?? 0)
  }));
}

/**
 * 平滑指的是**点之间**平滑，不是把点本身抹平。
 *
 * 曾经写成"相邻三点加权平均"，结果是用户把点拖到某处、线却不经过那里 ——
 * 点和线对不上，看着就像坏了。控制点是用户明确表达的意图，不能替他改；
 * 该平滑的是两点之间怎么过渡。
 *
 * 用 Catmull-Rom：曲线严格穿过每个控制点，相邻段的斜率连续，所以转折处圆滑。
 * 结果夹回 [0,100] —— 这条样条在陡峭段会冲出控制点的范围，而转速没有负的也没有超过满转的。
 */
function catmullRom(
  previous: number,
  start: number,
  end: number,
  next: number,
  t: number
): number {
  const t2 = t * t;
  const t3 = t2 * t;
  const value = 0.5 * (
    (2 * start)
    + ((-previous + end) * t)
    + (((2 * previous) - (5 * start) + (4 * end) - next) * t2)
    + ((-previous + (3 * start) - (3 * end) + next) * t3));
  return clamp(value, minimumPercent, maximumPercent);
}

/**
 * 这条曲线在某个温度下要求多大转速。
 *
 * 用的是**平滑之后**的值：平滑是曲线语义的一部分，不是界面上的装饰，
 * 所以预览看到的和写下去的必须是同一条。
 *
 * 曲线之外不外推 —— 比最低点还低就用最低点，比最高点还高就用最高点。
 */
export function percentAt(
  points: readonly ControlCurvePoint[] | null | undefined,
  temperatureCelsius: number
): number {
  const stops = stopsFromCurve(points);
  if (temperatureCelsius <= curveStops[0]) {
    return stops[0];
  }
  const lastIndex = curveStops.length - 1;
  if (temperatureCelsius >= curveStops[lastIndex]) {
    return stops[lastIndex];
  }

  for (let index = 1; index <= lastIndex; index++) {
    if (temperatureCelsius > curveStops[index]) {
      continue;
    }
    const span = curveStops[index] - curveStops[index - 1];
    const t = span <= 0 ? 0 : (temperatureCelsius - curveStops[index - 1]) / span;
    // 两端之外没有邻居，就拿端点自己顶上 —— 等价于在端点处不再继续弯。
    return catmullRom(
      stops[Math.max(0, index - 2)],
      stops[index - 1],
      stops[index],
      stops[Math.min(lastIndex, index + 1)],
      t);
  }
  return stops[lastIndex];
}

function interpolate(
  curve: readonly { temperatureCelsius: number; percent: number }[],
  temperatureCelsius: number
): number {
  if (curve.length === 0) {
    return 0;
  }
  if (temperatureCelsius <= curve[0].temperatureCelsius) {
    return curve[0].percent;
  }
  const last = curve[curve.length - 1];
  if (temperatureCelsius >= last.temperatureCelsius) {
    return last.percent;
  }
  for (let index = 1; index < curve.length; index++) {
    const upper = curve[index];
    if (temperatureCelsius > upper.temperatureCelsius) {
      continue;
    }
    const lower = curve[index - 1];
    const span = upper.temperatureCelsius - lower.temperatureCelsius;
    if (span <= 0) {
      return upper.percent;
    }
    const ratio = (temperatureCelsius - lower.temperatureCelsius) / span;
    return lower.percent + ((upper.percent - lower.percent) * ratio);
  }
  return last.percent;
}

/**
 * 曲线是不是单调不降。
 *
 * 温度升高转速却下降，几乎总是调错了。这里只回答是不是，**不自动改** ——
 * 用户可能真想要一段回落，替他改掉比让他看见问题更糟。
 */
export function isMonotonic(percents: readonly number[]): boolean {
  const values = curveStops.map((_, index) => sanitizePercent(percents[index] ?? 0));
  for (let index = 1; index < values.length; index++) {
    if (values[index] < values[index - 1]) {
      return false;
    }
  }
  return true;
}
