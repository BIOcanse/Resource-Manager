import assert from "node:assert/strict";
import { formatFieldValue } from "../src/presentation/fieldValueFormat.ts";
import { applyByteUnitMode } from "../src/presentation/byteUnits.ts";

applyByteUnitMode("native");
const v = (unit: string, extra: Partial<{ numericValue: number | null; integerValue: bigint | null; booleanValue: boolean | null }>) =>
  ({ name: "f", unit, valueKind: "float64", numericValue: null, integerValue: null, booleanValue: null, observedAt: null, ...extra });

assert.equal(formatFieldValue(v("%", {})), null, "unavailable is null, never 0");
assert.equal(formatFieldValue(v("%", { numericValue: 137.54 })), "137.5%", "percent is not clamped");
assert.equal(formatFieldValue(v("%", { numericValue: 0 })), "0.0%", "a valid zero stays zero");
assert.equal(formatFieldValue(v("°C", { numericValue: 61.26 })), "61.3 °C");
assert.equal(formatFieldValue(v("MHz", { numericValue: 4812.6 })), "4813 MHz");
assert.equal(formatFieldValue(v("W", { numericValue: 12.5 })), "12.5 W");
assert.equal(formatFieldValue(v("B", { integerValue: 1073741824n })), "1.00 GiB");
assert.equal(formatFieldValue(v("B", { integerValue: 1000000000n }), "storage"), "1.00 GB");
assert.equal(formatFieldValue(v("MiB", { integerValue: 32768n })), "32.0 GiB");
assert.equal(formatFieldValue(v("count", { integerValue: 340282366920938463463374607431768211455n })),
  (340282366920938463463374607431768211455n).toLocaleString(), "unsigned128 counts stay exact");
assert.equal(formatFieldValue(v("bitmask64", { integerValue: 255n })), "0xFF");
assert.equal(formatFieldValue(v("flag", { booleanValue: false })), "否", "a false flag is shown, not hidden");
assert.equal(formatFieldValue(v("J/count", { numericValue: 0.0000152587890625 })), "0.00001526 J/count");
assert.equal(formatFieldValue(v("MB", { integerValue: 8176n })), "8176 MB", "unknown byte multiplier is not converted");
console.log("fieldValueFormat: ok");
