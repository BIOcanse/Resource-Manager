import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const settingsPage = readSource("../src/components/SettingsPage.tsx");
const settingsLoader = readSource("../src/i18n/settingsLoader.ts");
const settingsLocaleFactory = readSource("../src/i18n/settingsLocaleFactory.ts");
const appearanceSettings = readSource("../src/components/settings/AppearanceSettingsSection.tsx");

assert.match(settingsPage, /createSignal<SettingsTextBundle>\(fallbackSettingsText\)/);
assert.doesNotMatch(settingsPage, /createSignal<SettingsTextBundle>\(loadingSettingsText\)/);
assert.match(settingsLoader, /return fallbackSettingsText;/);
assert.match(settingsLoader, /createSettingsLocale\("zh-CN"\)/);
assert.match(settingsPage, /<nav class="settings-nav" aria-label=\{text\(\)\.navigationLabel\}>/);
assert.match(settingsLoader, /navigationLabel: ""/);
assert.match(appearanceSettings, /appearance\.settingsLanguageTitle/);
assert.match(appearanceSettings, /appearance\.settingsLanguageDescription/);
assert.match(appearanceSettings, /appearance\.barColorTitle/);
assert.match(appearanceSettings, /appearance\.barColorDescription/);
assert.match(appearanceSettings, /text\.barColorOptions/);
assert.match(settingsLocaleFactory, /settingsLanguageTitle: "设置页语言"/);
assert.match(settingsLocaleFactory, /type: \{ label: "类型固定色"/);
assert.match(settingsLocaleFactory, /distinct: \{ label: "异色区分"/);
assert.match(settingsLocaleFactory, /Settings page only/);
assert.match(settingsLoader, /settingsLanguageDescription: ""/);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
