/** Shipped aliases used .NET custom 0 / 0.# / 0.## / 0.### formatting, whose decimal
 * buffer has 15 significant digits and rounds decimal midpoints away from zero.
 * Work on that decimal buffer so binary toFixed rounding cannot change a reading.
 * Called only for finite numeric facts and these four established alias scales.
 */
export function formatLegacyMetricNumber(value: number, scale: 0 | 1 | 2 | 3): string {
  const [mantissa, exponent = "0"] = Math.abs(value).toPrecision(15).split("e");
  const fractionLength = mantissa.includes(".") ? mantissa.length - mantissa.indexOf(".") - 1 : 0;
  const coefficient = BigInt(mantissa.replace(".", ""));
  const shift = Number(exponent) - fractionLength + scale;
  const divisor = shift < 0 ? 10n ** BigInt(-shift) : 1n;
  const rounded = shift < 0
    ? (coefficient + divisor / 2n) / divisor
    : coefficient * 10n ** BigInt(shift);
  const sign = value < 0 || Object.is(value, -0) ? "-" : "";
  if (rounded === 0n) return `${sign}0`;
  const digits = rounded.toString().padStart(scale + 1, "0");
  const fixed = scale === 0 ? digits
    : `${digits.slice(0, -scale)}.${digits.slice(-scale)}`.replace(/\.?0+$/, "");
  return sign + fixed;
}
