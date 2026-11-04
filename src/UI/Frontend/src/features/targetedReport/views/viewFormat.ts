import { byteUnitScale, formatByteUnitValue } from "../../../presentation/byteUnits.ts";

/** Share byte scaling with other views, display time axes in seconds, and retain other units with at most two decimals. */
export interface UnitScale { unit: string; format: (value: number) => string }

const trimmed = (value: number) => String(Number(value.toFixed(2)));

export function unitScale(unit: string, largest: number): UnitScale {
  if (unit === "B" || unit === "B/s") {
    const scale = byteUnitScale(Math.abs(largest), unit === "B" ? "memory" : "storage");
    const suffix = unit === "B/s" ? "/s" : "";
    return { unit: scale.unit + suffix,
      format: (value) => formatByteUnitValue({ value: value / scale.divisor, unit: scale.unit }) + suffix };
  }
  return { unit, format: (value) => unit ? `${trimmed(value)} ${unit}` : trimmed(value) };
}

/** Convert milliseconds since recording start to seconds. */
export function formatElapsed(milliseconds: number) {
  return `${trimmed(milliseconds / 1000)} s`;
}

/** Keep units in axis titles and numbers in ticks. */
export function tickText(scale: UnitScale, value: number) {
  const text = scale.format(value);
  return scale.unit && text.endsWith(` ${scale.unit}`) ? text.slice(0, -scale.unit.length - 1) : text;
}
