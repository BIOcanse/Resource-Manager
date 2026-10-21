import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolveNumberFieldCommit } from "../src/features/settings/components/numberFieldModel.ts";

const settingsPage = readSource("../src/features/settings/SettingsPage.tsx");
const settingsLoader = readSource("../src/i18n/settingsLoader.ts");
const settingsLocaleFactory = readSource("../src/i18n/settingsLocaleFactory.ts");
const appearanceSettings = readSource("../src/features/settings/components/AppearanceSettingsSection.tsx");
const performanceSettings = readSource("../src/features/settings/components/PerformanceSettingsSection.tsx");
const settingsControls = readSource("../src/features/settings/components/SettingsControls.tsx");

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

// Refresh interval edits stay local until the shared NumberField commits them.
assert.match(performanceSettings, /<NumberField[\s\S]*?label=\{`\$\{itemText\(\)\.label\} \$\{props\.text\.performance\.refreshCadenceCustomLabel\}`\}[\s\S]*?min=\{500\}[\s\S]*?max=\{300000\}[\s\S]*?step=\{100\}[\s\S]*?onChange=\{\(value\) => updateLogicRefreshCustomValue\(key, value\)\}/);
assert.doesNotMatch(performanceSettings, /onInput=\{\(event\) => updateLogicRefreshCustomValue/);
assert.match(settingsControls, /const \[draft, setDraft\]/);
assert.match(settingsControls, /onBlur=\{commit\}/);
assert.match(settingsControls, /if \(event\.key === "Enter"\) event\.currentTarget\.blur\(\)/);
assert.equal(resolveNumberFieldCommit("2500", 1000, 100, [{ minimum: 500, maximum: 300000 }]), 2500);
assert.equal(resolveNumberFieldCommit("400", 1000, 100, [{ minimum: 500, maximum: 300000 }]), 500);
assert.equal(resolveNumberFieldCommit("300500", 1000, 100, [{ minimum: 500, maximum: 300000 }]), 300000);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
