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
assert.doesNotMatch(settingsLoader, /return fallbackSettingsText;|return loadSettingsText\("zh-CN"\)/);
assert.match(settingsLoader, /createLanguageBundleLoader<SettingsTextBundle>/);
assert.match(settingsLoader, /throw error;/);
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
// 可见标签只写“自定义 (ms)”，不再把整行标题重复一遍；读屏名称仍带行标题，三行可区分。
assert.match(performanceSettings, /<NumberField[\s\S]*?label=\{props\.text\.performance\.refreshCadenceCustomLabel\}[\s\S]*?ariaLabel=\{`\$\{itemText\(\)\.label\} \$\{props\.text\.performance\.refreshCadenceCustomLabel\}`\}[\s\S]*?min=\{500\}[\s\S]*?max=\{300000\}[\s\S]*?step=\{100\}[\s\S]*?onChange=\{\(value\) => updateLogicRefreshCustomValue\(key, value\)\}/);
assert.match(settingsControls, /aria-label=\{props\.ariaLabel\}/);
// 只显示当前模式生效的输入：锁定预设才有预设下拉框，自定义才有毫秒输入框，自动调度两者都没有。
assert.match(performanceSettings, /<Match when=\{setting\(\)\.mode === "preset"\}>\s*<label class="settings-select settings-inline-select">[\s\S]*?<StandardSelect/);
assert.match(performanceSettings, /<Match when=\{setting\(\)\.mode === "custom"\}>\s*<NumberField/);
assert.equal(performanceSettings.match(/<StandardSelect/g)?.length, 1);
assert.equal(performanceSettings.match(/<NumberField/g)?.length, 1);
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
