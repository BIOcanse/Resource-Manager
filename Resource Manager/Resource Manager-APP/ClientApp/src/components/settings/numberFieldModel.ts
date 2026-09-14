export interface NumberFieldRange {
  minimum: number;
  maximum: number;
}

export function normalizeNumberFieldRanges(
  ranges: readonly NumberFieldRange[]
): NumberFieldRange[] {
  const ordered = ranges
    .filter((range) => Number.isFinite(range.minimum)
      && Number.isFinite(range.maximum)
      && range.maximum >= range.minimum)
    .map((range) => ({ minimum: range.minimum, maximum: range.maximum }))
    .sort((left, right) => left.minimum - right.minimum || left.maximum - right.maximum);
  const result: NumberFieldRange[] = [];

  for (const range of ordered) {
    const previous = result.at(-1);
    if (previous && range.minimum <= previous.maximum) {
      previous.maximum = Math.max(previous.maximum, range.maximum);
      continue;
    }

    result.push(range);
  }

  return result;
}

export function intersectNumberFieldRanges(
  ranges: readonly NumberFieldRange[],
  minimum: number,
  maximum: number
): NumberFieldRange[] {
  if (!Number.isFinite(minimum) || !Number.isFinite(maximum) || maximum < minimum) {
    return [];
  }

  return normalizeNumberFieldRanges(ranges)
    .map((range) => ({
      minimum: Math.max(range.minimum, minimum),
      maximum: Math.min(range.maximum, maximum)
    }))
    .filter((range) => range.maximum >= range.minimum);
}

export function snapNumberToRanges(
  value: number,
  ranges: readonly NumberFieldRange[]
): number | null {
  if (!Number.isFinite(value)) return null;
  const normalized = normalizeNumberFieldRanges(ranges);
  if (normalized.length === 0) return null;

  let closest = normalized[0].minimum;
  let closestDistance = Math.abs(value - closest);
  for (const range of normalized) {
    if (value >= range.minimum && value <= range.maximum) return value;
    for (const candidate of [range.minimum, range.maximum]) {
      const distance = Math.abs(value - candidate);
      if (distance < closestDistance || (distance === closestDistance && candidate < closest)) {
        closest = candidate;
        closestDistance = distance;
      }
    }
  }

  return closest;
}

export function resolveNumberFieldCommit(
  raw: string,
  previousValue: number,
  step: number,
  ranges: readonly NumberFieldRange[]
): number {
  const normalizedRaw = raw.trim();
  if (normalizedRaw.length === 0) return previousValue;
  const parsed = Number(normalizedRaw);
  if (!Number.isFinite(parsed)) return previousValue;

  const normalizedStep = Number.isFinite(step) && step > 0 ? step : 1;
  const precision = decimalPlaces(normalizedStep);
  const stepped = Number((Math.round(parsed / normalizedStep) * normalizedStep).toFixed(precision));
  const snapped = snapNumberToRanges(stepped, ranges);
  return snapped == null ? previousValue : Number(snapped.toFixed(precision));
}

export function formatNumberFieldValue(value: number, step: number) {
  if (!Number.isFinite(value)) return "";
  const precision = decimalPlaces(Number.isFinite(step) && step > 0 ? step : 1);
  return Number(value.toFixed(precision)).toString();
}

function decimalPlaces(value: number) {
  const text = value.toString().toLowerCase();
  if (text.includes("e-")) {
    const [coefficient, exponent] = text.split("e-");
    const coefficientDecimals = coefficient.includes(".")
      ? coefficient.length - coefficient.indexOf(".") - 1
      : 0;
    return Number(exponent) + coefficientDecimals;
  }

  return text.includes(".") ? text.length - text.indexOf(".") - 1 : 0;
}
