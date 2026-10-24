import { portProtocol } from "../src/features/deviceTopology/deviceVocabulary.ts";
import { createComputed, createRoot } from "solid-js";
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
import { createLanguageBundleLoader } from "../src/i18n/languageBundleLoader.ts";
import { languageFailureText } from "../src/i18n/languageFailureText.ts";
import type { LanguageLoadError } from "../src/i18n/appTextStore.ts";
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

// Both production bundle loaders use the same failure-evicting cache. Test each
// failure independently: neither half may change the visible language alone.
for (const failedBundle of ["appCopy", "settingsText"] as const) {
  let attempts = 0;
  const failure = new Error(`${failedBundle} download failed`);
  const cachedCopy = createLanguageBundleLoader(async (language) => {
    if (language === "ja-JP" && failedBundle === "appCopy" && attempts++ === 0) throw failure;
    return language === "ja-JP" ? japaneseCopy : englishCopy;
  });
  const cachedSettings = createLanguageBundleLoader(async (language) => {
    if (language === "ja-JP" && failedBundle === "settingsText" && attempts++ === 0) throw failure;
    return language === "ja-JP" ? japaneseSettings : englishSettings;
  });
  const visible: string[] = [];
  const errors: Array<LanguageLoadError | null> = [];
  const apply = createLanguageApplier({ appCopy: cachedCopy.load, settingsText: cachedSettings.load },
    (language, copy, settings) => {
      assert.equal(copy.page.monitor, language === "ja-JP" ? japaneseCopy.page.monitor : englishCopy.page.monitor);
      assert.equal(settings.language, language);
      visible.push(language);
    }, (error) => errors.push(error));
  await apply("en-US");
  await assert.rejects(apply("ja-JP"), (error) => error === failure);
  assert.deepEqual(visible, ["en-US"], `${failedBundle}: retain last fully loaded bundles`);
  assert.equal(errors.at(-1)?.language, "ja-JP");
  assert.equal(errors.at(-1)?.cause, failure);
  const failedCache = failedBundle === "appCopy" ? cachedCopy : cachedSettings;
  assert.equal(failedCache.isReady("ja-JP"), false);
  await apply("ja-JP");
  assert.equal(attempts, 2, "explicit retry must perform a fresh failed-bundle load");
  assert.equal(failedCache.isReady("ja-JP"), true);
  assert.deepEqual(visible, ["en-US", "ja-JP"]);
  assert.equal(errors.at(-1), null);
}

// A late failure of an abandoned request must not replace the current state.
const staleCopy = deferred<AppCopy>();
const staleErrors: Array<LanguageLoadError | null> = [];
const applyStale = createLanguageApplier({
  appCopy: language => language === "ja-JP" ? staleCopy.promise : Promise.resolve(englishCopy),
  settingsText: () => Promise.resolve(englishSettings)
}, () => undefined, error => staleErrors.push(error));
await applyStale("en-US");
const abandoned = applyStale("ja-JP");
const abandonedResult = assert.rejects(abandoned, /abandoned download/u);
await applyStale("en-US");
staleCopy.reject(new Error("abandoned download"));
await abandonedResult;
assert.ok(staleErrors.every(error => error === null));

// Repeating the same target does not let its older unfinished request commit.
const oldCopy = deferred<AppCopy>();
const newCopy = deferred<AppCopy>();
let copyRequests = 0;
const sameTargetCommits: string[] = [];
const sameTarget = createLanguageApplier({
  appCopy: () => ++copyRequests === 1 ? oldCopy.promise : newCopy.promise,
  settingsText: () => Promise.resolve(japaneseSettings)
}, language => sameTargetCommits.push(language));
const older = sameTarget("ja-JP");
const newer = sameTarget("ja-JP");
oldCopy.resolve(japaneseCopy);
await older;
assert.deepEqual(sameTargetCommits, []);
newCopy.resolve(japaneseCopy);
await newer;
assert.deepEqual(sameTargetCommits, ["ja-JP"]);

// Reactive consumers see complete bundles, document attributes and the shell
// notification from the same commit, never an intermediate language.
const previousDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
const previousWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
const documentElement = { lang: "", dir: "" };
const messages: Array<{ type: string; language: string }> = [];
Object.defineProperty(globalThis, "document", { configurable: true, value: { documentElement } });
Object.defineProperty(globalThis, "window", { configurable: true,
  value: { chrome: { webview: { postMessage: (message: { type: string; language: string }) => messages.push(message) } } } });
let dispose: () => void = () => undefined;
try {
  await applyLanguage("en-US");
  const expected = await loadAppCopy("de-DE");
  createRoot(cleanup => {
    dispose = cleanup;
    createComputed(() => {
      if (currentLanguage() !== "de-DE") return;
      assert.equal(currentSettingsText().language, "de-DE");
      assert.equal(uiText.page.monitor, expected.page.monitor);
      assert.equal(documentElement.lang, "de-DE");
      assert.equal(documentElement.dir, "ltr");
      assert.deepEqual(messages.at(-1), { type: "shell.language", language: "de-DE" });
    });
  });
  await applyLanguage("de-DE");
  assert.equal(documentElement.lang, "de-DE");
} finally {
  dispose();
  if (previousDocument) Object.defineProperty(globalThis, "document", previousDocument);
  else Reflect.deleteProperty(globalThis, "document");
  if (previousWindow) Object.defineProperty(globalThis, "window", previousWindow);
  else Reflect.deleteProperty(globalThis, "window");
}

const shippedLanguages = languageOptions
  .map((option) => option.id)
  .filter((language): language is ConcreteAppLanguageMode => language !== "system");
assert.equal(shippedLanguages.length, 9);
const englishAppCopy = await loadAppCopy("en-US");
for (const language of shippedLanguages) {
  const failureText = languageFailureText(language);
  assert.ok(failureText.message.trim(), `${language}: failure message`);
  assert.ok(failureText.retry.trim(), `${language}: retry action`);
  if (language !== "en-US") {
    assert.notEqual(failureText.message, languageFailureText("en-US").message);
    assert.notEqual(failureText.retry, languageFailureText("en-US").retry);
  }
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
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

for (const language of languageOptions.filter(option => option.id !== "system")) {
  if (language.id === "system") continue;
  applyLanguage(language.id);
  await Promise.all([loadAppCopy(language.id), loadSettingsText(language.id)]);
  await Promise.resolve();
  assert.equal(currentLanguage(), language.id);
  const copy = await loadAppCopy(language.id);
  assert.equal(portProtocol({ protocol: null, display: null }), copy.deviceTopology.deviceCategory.unknown);
  assert.ok(portProtocol({ protocol: null, display: null }).trim());
  assert.equal(portProtocol({ protocol: "USB4", display: null }), "USB4");
}
