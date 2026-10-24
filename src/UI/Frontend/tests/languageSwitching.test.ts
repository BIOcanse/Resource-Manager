import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { languageOptions, normalizeLanguageMode, resolveLanguageMode } from "../src/i18n/settingsLanguages.ts";
import {
  applyLanguage,
  createLanguageApplier,
  currentLanguage,
  currentSettingsText,
  uiText
} from "../src/i18n/appTextStore.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { operationTitleForKind } from "../src/frontendRuntime/taskCenter/TaskCenterProjection.ts";
import type { AppCopy } from "../src/i18n/copy/index.ts";
import type {
  ConcreteAppLanguageMode,
  SettingsTextBundle
} from "../src/i18n/settingsTypes.ts";

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

// Copy and settings load at different speeds. A must remain fully applied when
// B's settings finish first and the user returns to A before B's copy arrives.
const [englishCopy, englishSettings] = await Promise.all([
  loadAppCopy("en-US"),
  loadSettingsText("en-US")
]);
const [japaneseCopy, japaneseSettings] = await Promise.all([
  loadAppCopy("ja-JP"),
  loadSettingsText("ja-JP")
]);
const pendingJapaneseCopy = deferred<AppCopy>();
const pendingJapaneseSettings = deferred<SettingsTextBundle>();
const committedBundles: Array<{
  language: string;
  appCopy: AppCopy;
  settingsText: SettingsTextBundle;
}> = [];
const applyControlledLanguage = createLanguageApplier(
  {
    appCopy: (language) => language === "ja-JP"
      ? pendingJapaneseCopy.promise
      : Promise.resolve(englishCopy),
    settingsText: (language) => language === "ja-JP"
      ? pendingJapaneseSettings.promise
      : Promise.resolve(englishSettings)
  },
  (language, appCopy, settingsText) => {
    committedBundles.push({ language, appCopy, settingsText });
  }
);

await applyControlledLanguage("en-US");
assert.equal(committedBundles.length, 1);
assert.equal(committedBundles[0].settingsText.language, "en-US");
const japaneseSwitch = applyControlledLanguage("ja-JP");
pendingJapaneseSettings.resolve(japaneseSettings);
await Promise.resolve();
assert.equal(committedBundles.length, 1, "settings must not commit before AppCopy");
await applyControlledLanguage("en-US");
pendingJapaneseCopy.resolve(japaneseCopy);
await japaneseSwitch;
assert.equal(committedBundles.length, 1, "returning to applied A cancels B's commit");
assert.equal(committedBundles[0].language, "en-US");
assert.equal(committedBundles[0].appCopy.page.monitor, englishCopy.page.monitor);
assert.equal(committedBundles[0].settingsText.language, "en-US");

const shippedLanguages = languageOptions
  .map((option) => option.id)
  .filter((language): language is ConcreteAppLanguageMode => language !== "system");
assert.equal(shippedLanguages.length, 9);
const englishAppCopy = await loadAppCopy("en-US");
for (const language of shippedLanguages) {
  const expectedCopy = await loadAppCopy(language);
  await applyLanguage(language);
  assert.equal(currentLanguage(), language);
  const operation = uiText.taskCenter.operation;
  const operationTitles = {
    "component.download": "componentDownload",
    "component.install": "componentInstall",
    "dependency.download": "dependencyDownload",
    "dependency.launch-installer": "dependencyLaunchInstaller",
    "software.uninstall": "softwareUninstall",
    "disk-usage.scan": "diskUsageScan"
  } as const;
  for (const [kind, key] of Object.entries(operationTitles)) {
    assert.equal(operation[key], expectedCopy.taskCenter.operation[key]);
    assert.equal(operationTitleForKind(kind), operation[key]);
    if (language !== "en-US") {
      assert.notEqual(operation[key], englishAppCopy.taskCenter.operation[key],
        `${language} taskCenter.operation.${key} must not inherit English`);
    }
  }
  assert.equal(operationTitleForKind("unsupported.operation"), operation.generic);
  assert.equal(operation.generic, expectedCopy.taskCenter.operation.generic);
  if (language !== "en-US") {
    assert.notEqual(operation.generic, englishAppCopy.taskCenter.operation.generic,
      `${language} taskCenter.operation.generic must not inherit English`);
  }
  assert.equal(uiText.taskCenter.taskFailed, expectedCopy.taskCenter.taskFailed);
  assert.equal(uiText.taskCenter.taskCompleted, expectedCopy.taskCenter.taskCompleted);
  if (language !== "en-US") {
    assert.notEqual(uiText.taskCenter.taskFailed, englishAppCopy.taskCenter.taskFailed,
      `${language} failure copy must not inherit English`);
    assert.notEqual(uiText.taskCenter.taskCompleted, englishAppCopy.taskCenter.taskCompleted,
      `${language} completion copy must not inherit English`);
  }
  for (const [key, value] of Object.entries(operation)) {
    assert.ok(value.trim().length > 0, `${language} taskCenter.operation.${key}`);
    assert.equal(value, expectedCopy.taskCenter.operation[key as keyof typeof expectedCopy.taskCenter.operation]);
  }
  const acquisition = uiText.componentAcquisition;
  for (const key of ["catalogUnavailable", "catalogPartial", "catalogError"] as const) {
    assert.ok(acquisition[key].trim().length > 0, `${language} componentAcquisition.${key}`);
    assert.equal(acquisition[key], expectedCopy.componentAcquisition[key]);
    if (language !== "en-US") {
      assert.notEqual(acquisition[key], englishAppCopy.componentAcquisition[key],
        `${language} componentAcquisition.${key} must not inherit English`);
    }
  }
  assert.equal(currentSettingsText().language, language);
}

const taskCenterDialog = await readFile(
  new URL("../src/ui/task-center/TaskCenterDialog.tsx", import.meta.url),
  "utf8"
);
assert.doesNotMatch(taskCenterDialog, /const taskCenterTabs\s*=/u);
assert.match(taskCenterDialog, /const visibleTabs = createMemo\(\(\) => \{/u);
assert.match(taskCenterDialog, /label: uiText\.taskCenter\.tabActive/u);
assert.match(taskCenterDialog, /<strong>\{taskTitle\(item\(\)\)\}<\/strong>/u);
assert.doesNotMatch(taskCenterDialog, /userFacingMessage|progress\(\)\.message|resultSummary|errorSummary/u);
assert.match(taskCenterDialog, /item\(\)\.status === "failed"/u);
assert.match(taskCenterDialog, /uiText\.taskCenter\.taskFailed/u);
assert.match(taskCenterDialog, /uiText\.taskCenter\.taskCompleted/u);
const acquisitionDialog = await readFile(
  new URL("../src/features/management/components/ComponentAcquisitionDialog.tsx", import.meta.url),
  "utf8"
);
assert.doesNotMatch(acquisitionDialog, /language\.startsWith\("zh"\)/u);
for (const key of ["catalogUnavailable", "catalogPartial", "catalogError"]) {
  assert.match(acquisitionDialog, new RegExp(`componentAcquisition\\.${key}`, "u"));
}

console.log("Language normalization, script matching, lazy-load cancellation and switching passed.");

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
