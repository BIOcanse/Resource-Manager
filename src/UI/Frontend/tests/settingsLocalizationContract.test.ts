import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolveNumberFieldCommit } from "../src/features/settings/components/numberFieldModel.ts";

const settingsPage = readSource("../src/features/settings/SettingsPage.tsx");
const settingsLoader = readSource("../src/i18n/settingsLoader.ts");
const settingsLocaleFactory = readSource("../src/i18n/settingsLocaleFactory.ts");
const appearanceSettings = readSource("../src/features/settings/components/AppearanceSettingsSection.tsx");
const refreshCadenceSetting = readSource("../src/features/settings/components/RefreshCadenceSetting.tsx");
const settingsControls = readSource("../src/features/settings/components/SettingsControls.tsx");
const updatesSettings = readSource("../src/features/settings/components/UpdatesSettingsSection.tsx");
const notificationsSettings = readSource("../src/features/settings/components/NotificationsSettingsSection.tsx");
const trustedAccessManager = readSource("../src/features/settings/components/TrustedAccessManager.tsx");
const trustedAccessApi = readSource("../src/features/trustedAccess/trustedAccessApi.ts");
const accessRequestSnapshotContext = readSource("../src/features/trustedAccess/AccessRequestSnapshotContext.tsx");
const trustedAccessBanner = readSource("../src/features/settings/components/TrustedAccessPendingBanner.tsx");

// 设置页不再自己持有一份文案：界面语言只有 appTextStore 一个所有者。
assert.doesNotMatch(settingsPage, /createSignal<SettingsTextBundle>/);
assert.doesNotMatch(settingsPage, /loadSettingsText\(/);
assert.match(settingsPage, /const text = \(\) => currentSettingsText\(\);/);
assert.doesNotMatch(settingsLoader, /return fallbackSettingsText;|return loadSettingsText\("zh-CN"\)/);
assert.match(settingsLoader, /createLanguageBundleLoader<SettingsTextBundle>/);
assert.match(settingsLoader, /throw error;/);
assert.match(settingsLoader, /createSettingsLocale\("zh-CN"\)/);
assert.match(settingsPage, /<nav ref=\{nav\} class="settings-nav has-selection-indicator" aria-label=\{text\(\)\.navigationLabel\}>/);
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
// Notification switches live in the notifications section (docs/notifications_2026-10-29.md), labelled per kind.
assert.doesNotMatch(updatesSettings, /notifications|SwitchControl ariaLabel=\{title\}/);
assert.match(notificationsSettings, /ariaLabel=\{text\(\)\.switchLabel\(kind\(\)\.title, title\(\)\)\}/);
assert.match(notificationsSettings, /text\(\)\.kinds\[entry\.kind\]/);

// Trusted-access requests use the shared backend subscription source; the caller facts are informational,
// scopes shown for approval come only from the request, and the default secret has no frontend read path.
assert.match(accessRequestSnapshotContext, /usePushSource\(runtime\.sources\.accessRequests,/);
assert.equal((accessRequestSnapshotContext.match(/usePushSource\(runtime\.sources\.accessRequests/g) ?? []).length, 1);
assert.match(trustedAccessManager, /useAccessRequestSnapshot\(\)/);
assert.match(trustedAccessBanner, /useAccessRequestSnapshot\(\)/);
assert.match(trustedAccessBanner, /reviewRequests/);
assert.doesNotMatch(trustedAccessManager, /runtime\.sources\.accessRequests/);
assert.match(trustedAccessManager, /For each=\{request\(\)\.requestedScopes\}/);
assert.match(trustedAccessManager, /observedCallerIsInformational/);
assert.match(trustedAccessManager, /defaultSecretLocalOnly/);
assert.match(trustedAccessManager, /<strong>\{props\.copy\.defaultKeyName\}<\/strong>/);
assert.doesNotMatch(trustedAccessManager, /localStorage|sessionStorage|setInterval|\bfetch\s*\(/);
assert.match(trustedAccessApi, /"\/api\/access-keys\/default"/);
assert.doesNotMatch(trustedAccessApi, /acquire-default|defaultSecret/);

// Refresh interval edits stay local until the shared NumberField commits them.
// 可见标签只写“自定义 (ms)”，不再把整行标题重复一遍；读屏名称仍带行标题，三行可区分。
assert.match(refreshCadenceSetting, /<NumberField[\s\S]*?label=\{props\.text\.performance\.refreshCadenceCustomLabel\}[\s\S]*?ariaLabel=\{`\$\{itemText\(\)\.label\} \$\{props\.text\.performance\.refreshCadenceCustomLabel\}`\}[\s\S]*?min=\{500\}[\s\S]*?max=\{300000\}[\s\S]*?step=\{100\}[\s\S]*?onChange=\{\(value\) => updateLogicRefreshCustomValue\(key, value\)\}/);
assert.match(settingsControls, /aria-label=\{props\.ariaLabel\}/);
// 只显示当前模式生效的输入：锁定预设才有预设下拉框，自定义才有毫秒输入框，自动调度两者都没有。
assert.match(refreshCadenceSetting, /<Match when=\{setting\(\)\.mode === "preset"\}>\s*<label class="settings-select settings-inline-select">[\s\S]*?<StandardSelect/);
assert.match(refreshCadenceSetting, /<Match when=\{setting\(\)\.mode === "custom"\}>\s*<NumberField/);
assert.equal(refreshCadenceSetting.match(/<StandardSelect/g)?.length, 1);
assert.equal(refreshCadenceSetting.match(/<NumberField/g)?.length, 1);
assert.doesNotMatch(refreshCadenceSetting, /onInput=\{\(event\) => updateLogicRefreshCustomValue/);
assert.match(settingsControls, /const \[draft, setDraft\]/);
assert.match(settingsControls, /onBlur=\{commit\}/);
assert.match(settingsControls, /if \(event\.key === "Enter"\) event\.currentTarget\.blur\(\)/);
assert.equal(resolveNumberFieldCommit("2500", 1000, 100, [{ minimum: 500, maximum: 300000 }]), 2500);
assert.equal(resolveNumberFieldCommit("400", 1000, 100, [{ minimum: 500, maximum: 300000 }]), 500);
assert.equal(resolveNumberFieldCommit("300500", 1000, 100, [{ minimum: 500, maximum: 300000 }]), 300000);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
