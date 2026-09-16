import type { ControlCurvePoint } from "./controlTypes.ts";

/**
 * 风扇曲线：温度到转速的折线。
 *
 * 纯函数，不碰界面也不碰硬件 —— 不管最后由 NBFC 还是显卡驱动去写，
 * 曲线本身的规矩是一样的，所以放在这里而不是某个写入器里。
 */

/** 曲线点的合法范围。超出的一律夹进来，不报错也不丢点。 */
export const minimumTemperature = 0;
export const maximumTemperature = 110;
export const minimumPercent = 0;
export const maximumPercent = 100;

/** 曲线最多这么多点。够用，也挡住"往里塞几千个点"这种输入。 */
export const maximumPoints = 16;

function clamp(value: number, low: number, high: number) {
  return value < low ? low : value > high ? high : value;
}

/**
 * 把用户编出来的点收拾成一条能用的曲线。
 *
 * 按温度排序、夹进合法范围、同温度只留最后一个、超量截断。
 * **收拾在这里做一次**，写入器和界面都不用再各自判断一遍。
 */
export function normalizeCurve(
  points: readonly ControlCurvePoint[]
): ControlCurvePoint[] {
  const byTemperature = new Map<number, number>();
  for (const point of points) {
    if (!Number.isFinite(point.temperatureCelsius) || !Number.isFinite(point.percent)) {
      continue;
    }
    const temperature = Math.round(
      clamp(point.temperatureCelsius, minimumTemperature, maximumTemperature));
    // 同一个温度重复给值时，后写的算数 —— 和用户拖动的直觉一致。
    byTemperature.set(temperature, Math.round(
      clamp(point.percent, minimumPercent, maximumPercent)));
  }

  return [...byTemperature.entries()]
    .sort(([left], [right]) => left - right)
    .slice(0, maximumPoints)
    .map(([temperatureCelsius, percent]) => ({ temperatureCelsius, percent }));
}

/**
 * 这条曲线在某个温度下要求多大转速。
 *
 * 两点之间线性插值；比第一个点还低就用第一个点，比最后一个点还高就用最后一个点 ——
 * 曲线之外不外推，外推出来的转速没有依据。
 */
export function percentAt(
  points: readonly ControlCurvePoint[],
  temperatureCelsius: number
): number {
  const curve = normalizeCurve(points);
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
 * 温度升高转速却下降，几乎总是编错了。这里只回答是不是，**不自动改** ——
 * 用户可能真想要一段回落，替他改掉比让他看见问题更糟。
 */
export function isMonotonic(points: readonly ControlCurvePoint[]): boolean {
  const curve = normalizeCurve(points);
  for (let index = 1; index < curve.length; index++) {
    if (curve[index].percent < curve[index - 1].percent) {
      return false;
    }
  }
  return true;
}

/** 一条温和的默认曲线，新建时用它起步，省得从空白开始编。 */
export function defaultCurve(): ControlCurvePoint[] {
  return [
    { temperatureCelsius: 40, percent: 0 },
    { temperatureCelsius: 55, percent: 30 },
    { temperatureCelsius: 70, percent: 55 },
    { temperatureCelsius: 85, percent: 100 }
  ];
}
