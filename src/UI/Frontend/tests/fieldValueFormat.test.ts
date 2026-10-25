import assert from "node:assert/strict";
import { formatFieldValue } from "../src/presentation/fieldValueFormat.ts";
import { applyByteUnitMode } from "../src/presentation/byteUnits.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";

async function switchLanguage(language: string) {
  applyLanguage(language);
  for (let attempt = 0; currentLanguage() !== language && attempt < 100; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(currentLanguage(), language);
}

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
  (340282366920938463463374607431768211455n).toLocaleString(currentLanguage()), "unsigned128 counts stay exact");
assert.equal(formatFieldValue(v("bitmask64", { integerValue: 255n })), "0xFF");
assert.equal(formatFieldValue(v("flag", { booleanValue: false })), "否", "a false flag is shown, not hidden");
assert.equal(formatFieldValue(v("J/count", { numericValue: 0.0000152587890625 })), "0.00001526 J/count");
assert.equal(formatFieldValue(v("MB", { integerValue: 8176n })), "8176 MB", "unknown byte multiplier is not converted");

// 代码字段按字段名解码成当前语言的文字；认不出的代码显示原值。
const named = (name: string, unit: string, integerValue: bigint | null) => ({ ...v(unit, { integerValue }), name, valueKind: "unsigned64" });
const health = (code: bigint | null) => formatFieldValue(named("storage_all_health_status_reported", "enum", code));
assert.equal(health(0n), uiText.deviceTopology.storageHealth.healthy, "MSFT_PhysicalDisk 0 is healthy, matching the device details wording");
assert.equal(health(0n), "正常");
assert.equal(health(1n), uiText.deviceTopology.storageHealth.warning);
assert.equal(health(2n), uiText.deviceTopology.storageHealth.unhealthy);
assert.equal(health(5n), uiText.deviceTopology.storageHealth.unknown);
assert.equal(health(3n), "3", "an unknown health code shows its raw value");
assert.equal(health(null), null, "unavailable stays unavailable");
const power = (code: bigint | null) => formatFieldValue(named("battery_all_charge_state_actual", "bitmask64", code));
const flags = uiText.fieldValueLabel.batteryPowerState;
assert.equal(power(0x1n), flags.onLine, "0x1 is on AC power");
assert.equal(power(0x1n), "接通交流电");
assert.equal(power(0x5n), `${flags.onLine} · ${flags.charging}`, "several flags are all named");
assert.equal(power(0xAn), `${flags.discharging} · ${flags.critical}`);
assert.equal(power(0x0n), "0x0", "no flag at all shows the raw value");
assert.equal(power(0x11n), "0x11", "an unknown bit shows the raw value");
assert.equal(formatFieldValue(v("enum", { integerValue: 0n })), "0", "other enum fields keep the generic format");
await switchLanguage("en-US");
assert.equal(power(0x5n), "On AC power · Charging", "labels follow the current language");
assert.equal(health(0n), "Healthy");
await switchLanguage("zh-CN");
console.log("fieldValueFormat: ok");
