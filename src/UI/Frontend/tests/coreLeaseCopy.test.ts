import assert from "node:assert/strict";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { applyLanguage } from "../src/i18n/appTextStore.ts";
import { resourceFactUnavailableText } from "../src/presentation/metricLabels.ts";

const translated = new Map<string, string>();
const languages = languageOptions.filter(option => option.id !== "system");
assert.equal(languages.length, 9);
const oldClaims = /已应用|已應用|生效|fully applied|angewendet|aplicad[oa]|appliqué|適用|적용|применен|применён/i;
for (const { id } of languages) {
  const copy = await loadAppCopy(id);
  const settings = await loadSettingsText(id);
  const text = [copy.softwareActions.softwarePolicySaved, copy.softwareActions.processPolicySaved,
    copy.softwareActions.processPolicySavedRuntimePending, copy.softwareActions.processPolicySavedLaunchPending,
    copy.softwareActions.retryRuntimePublication, copy.misc.settingsSavedNotApplied,
    copy.misc.reapplySettingsFailed, settings.saveState.partial, settings.actions.reapply];
  for (const value of text) {
    assert.ok(value.trim(), id);
    assert.doesNotMatch(value, oldClaims, id);
  }
  if (!["zh-CN", "zh-TW", "ja-JP"].includes(id)) assert.ok(text.every(value => !/[\u3400-\u9fff]/.test(value)), `${id}: base-language prose`);
  assert.ok(!translated.has(text[0]), `${id}: copied policy success text from ${translated.get(text[0])}`);
  translated.set(text[0], id);
  await applyLanguage(id);
  for (const reason of ["cpu-topology-not-published", "cpu-residency-window-unavailable"]) {
    const expected = copy.resourceTable.unavailableReasons[reason];
    assert.ok(expected?.label && expected.hint, `${id}: ${reason}`);
    assert.notEqual(expected.label, reason);
    assert.deepEqual(resourceFactUnavailableText(reason), expected);
  }
  assert.deepEqual(resourceFactUnavailableText("future-backend-reason"), { label: "future-backend-reason", hint: "future-backend-reason" });
}
console.log("Nine-language publication wording and CPU reason presentation passed");
