import type { ControlCurvePoint } from "./controlTypes.ts";

/**
 * 风扇曲线：温度到转速。
 *
 * **调整点是固定的，每 5 度一个**，用户只调每个点的转速，不去拖动曲线本身。
 * 这样每次调整都落在同一组温度上，可预期、可对比、也不会拖出一堆密集又无意义的点；
 * 相邻点之间直线插值，与软件执行端一致。
 *
 * 纯函数，不碰界面也不碰硬件 —— 不管最后由 NBFC 还是显卡驱动去写，规矩是一样的。
 */

/** 转速百分比的合法范围。 */
export const minimumPercent = 0;
export const maximumPercent = 100;

/**
 * 固定的调整点。
 *
 * 保留既有十点编辑合同；固定档位固件按索引接收十个百分比。
 * 图表显示范围独立于这些控制点，两端之外保持端点值。
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
 * 相邻点直线插值，两端保持端点值。预览与执行使用相同语义。
 */
export function percentAt(
  points: readonly ControlCurvePoint[] | null | undefined,
  temperatureCelsius: number
): number {
  return interpolate((points ?? [])
    .filter(point => Number.isFinite(point.temperatureCelsius) && Number.isFinite(point.percent))
    .map(point => ({ ...point, percent: clamp(point.percent, minimumPercent, maximumPercent) }))
    .sort((left, right) => left.temperatureCelsius - right.temperatureCelsius), temperatureCelsius);
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
