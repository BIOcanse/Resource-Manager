import assert from "node:assert/strict";
import { languageOptions, normalizeLanguageMode, resolveLanguageMode } from "../src/i18n/settingsLanguages.ts";
import { applyLanguage, currentLanguage, currentSettingsText, uiText } from "../src/i18n/appTextStore.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";

for (const language of languageOptions.filter(option => option.id !== "system")) {
  assert.equal(normalizeLanguageMode(language.id.toUpperCase()), language.id);
  assert.equal(resolveLanguageMode(language.id), language.id);
}
assert.equal(normalizeLanguageMode(" EN_us "), "en-US");
assert.equal(normalizeLanguageMode("unknown"), "system");
for (const candidate of ["zh-Hant", "zh-Hant-SG", "ZH_tw", "zh-HK"]) {
  assert.equal(resolveLanguageMode("system", [candidate]), "zh-TW", candidate);
}
assert.equal(resolveLanguageMode("system", ["zh-Hans-SG"]), "zh-CN");
assert.equal(resolveLanguageMode("system", ["kl-GL", "en-GB"]), "en-US");
for (const candidate of ["nn-NO", "iw-IL", "in-ID"]) {
  assert.equal(resolveLanguageMode("system", [candidate]), "en-US", candidate);
}

async function loaded(language: "en-US" | "ja-JP") {
  await Promise.all([loadAppCopy(language), loadSettingsText(language)]);
  await Promise.resolve();
}
applyLanguage("en-US");
await loaded("en-US");
assert.equal(currentLanguage(), "en-US");
const englishTitle = uiText.page.monitor;

// Returning to the current language cancels an unfinished lazy language switch.
applyLanguage("ja-JP");
applyLanguage("en-US");
await loaded("ja-JP");
assert.equal(currentLanguage(), "en-US");
assert.equal(currentSettingsText().language, "en-US");
assert.equal(uiText.page.monitor, englishTitle);

applyLanguage("ja-JP");
await loaded("ja-JP");
assert.equal(currentLanguage(), "ja-JP");
assert.equal(currentSettingsText().language, "ja-JP");
assert.notEqual(uiText.page.monitor, englishTitle);
applyLanguage("en-US");
await loaded("en-US");
assert.equal(currentLanguage(), "en-US");
assert.equal(uiText.page.monitor, englishTitle);
console.log("Language normalization, script matching, lazy-load cancellation and switching passed.");
