import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const settingsPage = readSource("../src/features/settings/SettingsPage.tsx");
const settingsLoader = readSource("../src/i18n/settingsLoader.ts");
const settingsLocaleFactory = readSource("../src/i18n/settingsLocaleFactory.ts");
const appearanceSettings = readSource("../src/features/settings/components/AppearanceSettingsSection.tsx");

// 设置页不再自己持有一份文案：界面语言只有 appTextStore 一个所有者。
assert.doesNotMatch(settingsPage, /createSignal<SettingsTextBundle>/);
assert.doesNotMatch(settingsPage, /loadSettingsText\(/);
assert.match(settingsPage, /const text = \(\) => currentSettingsText\(\);/);
assert.match(settingsLoader, /return fallbackSettingsText;/);
assert.match(settingsLoader, /createSettingsLocale\("zh-CN"\)/);
assert.match(settingsPage, /<nav class="settings-nav" aria-label=\{text\(\)\.navigationLabel\}>/);
assert.match(settingsLoader, /navigationLabel: ""/);
assert.match(appearanceSettings, /appearance\.settingsLanguageTitle/);
assert.match(appearanceSettings, /appearance\.settingsLanguageDescription/);
assert.match(appearanceSettings, /appearance\.barColorTitle/);
assert.match(appearanceSettings, /appearance\.barColorDescription/);
assert.match(appearanceSettings, /text\.barColorOptions/);
assert.match(settingsLocaleFactory, /settingsLanguageTitle: "界面语言"/);
assert.match(settingsLocaleFactory, /type: \{ label: "类型固定色"/);
assert.match(settingsLocaleFactory, /distinct: \{ label: "异色区分"/);
assert.match(settingsLocaleFactory, /whole interface/);
assert.match(settingsLoader, /settingsLanguageDescription: ""/);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
