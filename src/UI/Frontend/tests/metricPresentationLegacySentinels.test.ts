import assert from "node:assert/strict";
import { metricCardReading, metricDisplayValue } from "../src/presentation/metricLabels.ts";
import { fieldCardValues } from "../src/features/monitor/fieldCards.ts";
import { applyLanguage, uiText } from "../src/text.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";

for (const language of languageOptions.filter(option => option.id !== "system")) {
  await applyLanguage(language.id);
  // Local field cards are real consumers of displayValue, including absent devices.
  const slot = { metricId: "field@missing", fieldName: "field", deviceIdentity: "missing" };
  const absent = fieldCardValues([slot], [], [])[slot.metricId];
  assert.equal(metricDisplayValue(absent), uiText.dashboard.deviceAbsent);
  assert.equal(metricCardReading(null, absent).missing, true);
  assert.equal(metricCardReading(null, absent).title, uiText.dashboard.deviceAbsentHint);
  // Display prose has no backend status semantics. Only empty values and local card states are missing.
  for (const displayValue of ["N/A", "-", "--"]) {
    const reading = metricCardReading(null, { displayValue });
    assert.equal(reading.value, displayValue, `${language.id}: preserve local display text ${displayValue}`);
    assert.equal(reading.missing, false, `${language.id}: ${displayValue} is not a raw status`);
  }
  for (const status of [0, 2, 3, 4, 5, 6, 7]) {
    const metric = { id: "cpu.usage", numericValue: 50, unit: "%", status, displayValue: "UNTRUSTED" };
    assert.equal(metricDisplayValue(metric), "--");
    assert.equal(metricCardReading(null, metric).value, uiText.dashboard.noReading);
    assert.equal(metricCardReading(null, metric).missing, true);
  }
}
console.log("Metric presentation preserves local field cards without legacy text sentinels.");
