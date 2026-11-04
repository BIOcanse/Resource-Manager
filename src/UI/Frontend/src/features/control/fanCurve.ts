import type { ControlCurvePoint } from "./controlTypes.ts";

/** Shared temperature-to-speed curve rules: fixed temperature coordinates and linear interpolation match execution. */

/** Valid speed percentage range. */
export const minimumPercent = 0;
export const maximumPercent = 100;

/** Temperature spacing between adjacent points. */
export const curveStepCelsius = 5;

/** Highest temperature coordinate. */
export const curveTopCelsius = 100;

/** Point count for curves without firmware limits. */
export const curveGridPoints = 15;

/** Fixed-step firmware display coordinates; the firmware owns the actual breakpoints. */
export const curveStops: readonly number[] =
  [40, 45, 50, 55, 60, 65, 70, 75, 80, 85];

/** Use fixed firmware stops or a regular grid constrained by the firmware point limit. */
export function curveTemperatures(fixedSteps: boolean, pointLimit: number | null): readonly number[] {
  if (fixedSteps) {
    const count = pointLimit ?? curveStops.length;
    return Array.from({ length: count }, (_, index) => curveStops[0] + index * curveStepCelsius);
  }
  const count = Math.max(1, Math.min(curveGridPoints, pointLimit ?? curveGridPoints));
  return Array.from({ length: count },
    (_, index) => curveTopCelsius - ((count - 1 - index) * curveStepCelsius));
}

/** Resample a curve onto the temperature coordinates that will be persisted. */
export function curveOnTemperatures(
  points: readonly ControlCurvePoint[] | null | undefined,
  temperatures: readonly number[]
): ControlCurvePoint[] {
  return curveFromStops(stopsFromCurve(points, temperatures), temperatures);
}

function clamp(value: number, low: number, high: number) {
  return value < low ? low : value > high ? high : value;
}

function sanitizePercent(value: number): number {
  return Number.isFinite(value) ? Math.round(clamp(value, minimumPercent, maximumPercent)) : 0;
}

/** Gentle default curve for initialization. */
export function defaultStops(): number[] {
  return [0, 15, 25, 35, 45, 55, 70, 85, 95, 100];
}

/** Interpolate saved curves onto editor coordinates so irregular or imported points remain usable. */
export function stopsFromCurve(
  points: readonly ControlCurvePoint[] | null | undefined,
  temperatures: readonly number[] = curveStops
): number[] {
  const curve = (points ?? [])
    .filter((point) =>
      Number.isFinite(point.temperatureCelsius) && Number.isFinite(point.percent))
    .map((point) => ({
      temperatureCelsius: point.temperatureCelsius,
      percent: sanitizePercent(point.percent)
    }))
    .sort((left, right) => left.temperatureCelsius - right.temperatureCelsius);

  if (curve.length === 0) {
    return temperatures.map(() => 0);
  }
  // Interpolation may yield fractions; editable speeds use integer percentages.
  // Round to a selectable percentage.
  return temperatures.map((temperature) => Math.round(interpolate(curve, temperature)));
}

/** Convert edited speeds into persistent curve points. */
export function curveFromStops(
  percents: readonly number[],
  temperatures: readonly number[] = curveStops
): ControlCurvePoint[] {
  return temperatures.map((temperatureCelsius, index) => ({
    temperatureCelsius,
    percent: sanitizePercent(percents[index] ?? 0)
  }));
}

/** Linearly interpolate between points and hold endpoint values outside the curve. */
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

/** Check monotonicity without rewriting intentional decreases. */
export function isMonotonic(percents: readonly number[]): boolean {
  const values = percents.map(sanitizePercent);
  for (let index = 1; index < values.length; index++) {
    if (values[index] < values[index - 1]) {
      return false;
    }
  }
  return true;
}
