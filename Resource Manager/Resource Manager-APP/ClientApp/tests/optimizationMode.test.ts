import assert from "node:assert/strict";
import {
  normalizeOptimizationModeValue,
  optimizationModeHasDomain,
  toggleOptimizationDomain
} from "../src/settings/optimizationMode.ts";
import type { AppOptimizationMode } from "../src/types.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import type { ConcreteAppLanguageMode } from "../src/i18n/settingsTypes.ts";

const modes: AppOptimizationMode[] = [
  "normal", "limited", "cpu", "gpu",
  "memory+cpu", "memory+gpu", "cpu+gpu", "smart"
];
for (const mode of modes) {
  assert.equal(normalizeOptimizationModeValue(mode), mode);
}
assert.equal(normalizeOptimizationModeValue("unsupported"), "normal");
assert.equal(normalizeOptimizationModeValue(null), "normal");

assert.equal(toggleOptimizationDomain("normal", "memory"), "limited");
assert.equal(toggleOptimizationDomain("normal", "cpu"), "cpu");
assert.equal(toggleOptimizationDomain("normal", "gpu"), "gpu");
assert.equal(toggleOptimizationDomain("limited", "cpu"), "memory+cpu");
assert.equal(toggleOptimizationDomain("memory+cpu", "gpu"), "smart");
assert.equal(toggleOptimizationDomain("smart", "memory"), "cpu+gpu");
assert.equal(toggleOptimizationDomain("cpu+gpu", "cpu"), "gpu");
assert.equal(toggleOptimizationDomain("gpu", "gpu"), "normal");

for (const mode of modes) {
  for (const domain of ["memory", "cpu", "gpu"] as const) {
    const next = toggleOptimizationDomain(mode, domain);
    assert.notEqual(optimizationModeHasDomain(next, domain), optimizationModeHasDomain(mode, domain));
    assert.equal(toggleOptimizationDomain(next, domain), mode);
  }
}

const english = await loadAppCopy("en-US");
const englishLabels = [
  english.optimization.modeNormal,
  english.optimization.modeLimited,
  english.optimization.modeCpu,
  english.optimization.modeGpu
];
for (const option of languageOptions) {
  if (option.id === "system") continue;
  const copy = await loadAppCopy(option.id as ConcreteAppLanguageMode);
  const labels = [
    copy.optimization.modeNormal,
    copy.optimization.modeLimited,
    copy.optimization.modeCpu,
    copy.optimization.modeGpu
  ];
  assert(labels.every((label) => label.trim().length > 0), `${option.id}: empty mode label`);
  assert.equal(new Set(labels).size, 4, `${option.id}: modes are not distinguishable`);
  assert.equal(copy.stores.smartModeSwitchFailed, copy.apiError.switchSmartModeFailed,
    `${option.id}: scheduling failure text differs across UI and API`);
  if (option.id !== "en-US") {
    assert.notDeepEqual(labels, englishLabels, `${option.id}: English fallback replaced localized modes`);
  }
}
